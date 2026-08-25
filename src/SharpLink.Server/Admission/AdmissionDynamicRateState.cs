using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Serializes one logical Global / Contract / Method rate lineage. Old generation states remain
/// independently usable, but every post-transition grant is conservatively charged to the current
/// state so overlapping generations cannot manufacture quota.
/// </summary>
internal sealed class AdmissionRateTransitionLineage
{
    internal Lock Gate { get; } = new();

    private AdmissionDynamicRateState? _current;

    internal AdmissionDynamicRateState? CurrentLocked => _current;

    internal void AttachFresh(AdmissionDynamicRateState state)
    {
        lock (Gate)
        {
            if (_current is not null)
                throw new InvalidOperationException("Admission rate lineage already has a current state.");
            _current = state;
        }
    }

    internal void CommitTransition(
        AdmissionDynamicRateState source,
        AdmissionDynamicRateState? target,
        long now)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_current, source))
            {
                throw new InvalidOperationException(
                    "Admission rate transition source is no longer the current logical rate state.");
            }

            target?.InitializeTransitionLocked(source, now);
            _current = target;
        }
    }

    internal void DetachIfCurrentLocked(AdmissionDynamicRateState state)
    {
        if (ReferenceEquals(_current, state))
            _current = null;
    }
}

/// <summary>
/// SharpLink-owned deterministic rate state. Configuration is immutable per program generation;
/// quota/history are mutable under the logical lineage lock and may be conservatively translated
/// into a prepared successor at publication time.
/// </summary>
internal sealed class AdmissionDynamicRateState : IDisposable
{
    private readonly AdmissionRateStateDefinition _definition;
    private readonly TimeProvider _timeProvider;
    private readonly long[] _slidingSegments;
    private RateWaiter? _waiterHead;
    private RateWaiter? _waiterTail;
    private ITimer? _timer;
    private long _tokenDebt;
    private long _tokenAnchor;
    private long _fixedConsumed;
    private long _fixedWindowStart;
    private long _slidingOwnTotal;
    private int _slidingCurrentSegment;
    private long _slidingSegmentStart;
    private long _transitionDebt;
    private long _transitionDebtExpiry;
    private long _latestGrantTimestamp = long.MinValue;
    private int _waitingCount;
    private int _disposed;

    internal AdmissionDynamicRateState(
        AdmissionRateStateDefinition definition,
        TimeProvider timeProvider,
        AdmissionRateTransitionLineage? lineage = null)
    {
        if (definition.Kind == AdmissionRateStateKind.None)
            throw new InvalidOperationException("Admission dynamic rate state requires one rate policy.");
        _definition = definition;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Lineage = lineage ?? new AdmissionRateTransitionLineage();
        _slidingSegments = definition.Kind == AdmissionRateStateKind.SlidingWindow
            ? new long[definition.Segments]
            : [];

        var now = _timeProvider.GetTimestamp();
        _tokenAnchor = now;
        _fixedWindowStart = now;
        _slidingSegmentStart = now;
        if (lineage is null)
            Lineage.AttachFresh(this);
    }

    internal AdmissionRateTransitionLineage Lineage { get; }

    internal AdmissionRateStateDefinition Definition => _definition;

    internal int WaitingCount
    {
        get
        {
            lock (Lineage.Gate)
                return _waitingCount;
        }
    }

    internal long TransitionDebtForDiagnostics
    {
        get
        {
            lock (Lineage.Gate)
            {
                AdvanceLocked(_timeProvider.GetTimestamp());
                return GetBurdenLocked();
            }
        }
    }

    internal long TransitionBarrierExpiryForDiagnostics
    {
        get
        {
            lock (Lineage.Gate)
            {
                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                return GetDebtExpiryLocked(now);
            }
        }
    }

    internal RateLimitLease AttemptAcquire(int permitCount)
    {
        ValidatePermitCount(permitCount);
        lock (Lineage.Gate)
        {
            if (_disposed != 0)
                return FailedLease.Instance;

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waitingCount != 0 || !CanGrantLocked())
                return FailedLease.Instance;

            RecordGrantLocked(now);
            return AcquiredLease.Instance;
        }
    }

    internal ValueTask<RateLimitLease> AcquireAsync(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        RateWaiter waiter;
        lock (Lineage.Gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waitingCount == 0 && CanGrantLocked())
            {
                RecordGrantLocked(now);
                return ValueTask.FromResult<RateLimitLease>(AcquiredLease.Instance);
            }

            waiter = new RateWaiter(this, cancellationToken);
            EnqueueLocked(waiter);
            ScheduleTimerLocked(now);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.UnsafeRegister(
                static state => ((RateWaiter)state!).Owner.CancelWaiter((RateWaiter)state!),
                waiter);
            waiter.SetRegistration(registration);
        }
        return new ValueTask<RateLimitLease>(waiter.Task);
    }

    internal void CommitTransitionTo(AdmissionDynamicRateState? target)
        => Lineage.CommitTransition(this, target, _timeProvider.GetTimestamp());

    internal void InitializeTransitionLocked(AdmissionDynamicRateState source, long now)
    {
        if (!ReferenceEquals(Lineage, source.Lineage))
            throw new InvalidOperationException("Admission rate transition target belongs to a different lineage.");
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(AdmissionDynamicRateState));

        source.AdvanceLocked(now);
        ResetPreparedTargetLocked(now);

        if (source._definition.Kind == _definition.Kind)
        {
            switch (_definition.Kind)
            {
                case AdmissionRateStateKind.TokenBucket:
                    _tokenDebt = source._tokenDebt;
                    _tokenAnchor = source._definition.Secondary == _definition.Secondary &&
                                   source._definition.PeriodTicks == _definition.PeriodTicks
                        ? source._tokenAnchor
                        : now;
                    _latestGrantTimestamp = source._latestGrantTimestamp;
                    break;
                case AdmissionRateStateKind.FixedWindow:
                    _fixedConsumed = source._fixedConsumed;
                    _fixedWindowStart = source._fixedWindowStart;
                    var targetWindow = GetWindowTimestampTicks();
                    if (now >= SaturatingAdd(_fixedWindowStart, targetWindow))
                        _fixedWindowStart = now;
                    _latestGrantTimestamp = source._latestGrantTimestamp;
                    break;
                case AdmissionRateStateKind.SlidingWindow:
                    InitializeConservativeBarrierLocked(source, now);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported admission rate transition kind.");
            }
        }
        else
        {
            InitializeConservativeBarrierLocked(source, now);
        }
    }

    private void InitializeConservativeBarrierLocked(AdmissionDynamicRateState source, long now)
    {
        var burden = source.GetBurdenLocked();
        _transitionDebt = burden;
        _latestGrantTimestamp = source._latestGrantTimestamp;
        _transitionDebtExpiry = burden == 0
            ? 0
            : GetConservativeTransitionExpiryLocked(source, now, burden);
    }

    private long GetConservativeTransitionExpiryLocked(
        AdmissionDynamicRateState source,
        long now,
        long burden)
    {
        var sourceExpiry = source.GetDebtExpiryLocked(now);
        var anchor = source._latestGrantTimestamp == long.MinValue
            ? now
            : source._latestGrantTimestamp;
        var targetExpiry = SaturatingAdd(anchor, GetBarrierHorizonTimestampTicks(burden));
        return Math.Max(sourceExpiry, targetExpiry);
    }

    private void ResetPreparedTargetLocked(long now)
    {
        _tokenDebt = 0;
        _tokenAnchor = now;
        _fixedConsumed = 0;
        _fixedWindowStart = now;
        _slidingOwnTotal = 0;
        _slidingCurrentSegment = 0;
        _slidingSegmentStart = now;
        if (_slidingSegments.Length != 0)
            Array.Clear(_slidingSegments);
        _transitionDebt = 0;
        _transitionDebtExpiry = 0;
        _latestGrantTimestamp = long.MinValue;
    }

    private void RecordGrantLocked(long now)
    {
        RecordOwnGrantLocked();
        _latestGrantTimestamp = Math.Max(_latestGrantTimestamp, now);

        var current = Lineage.CurrentLocked;
        if (current is null || ReferenceEquals(current, this))
            return;

        var sourceExpiry = GetDebtExpiryLocked(now);
        current.AdvanceLocked(now);
        current.RecordLegacyGrantLocked(now, sourceExpiry);
        current.ScheduleTimerLocked(now);
    }

    private void RecordOwnGrantLocked()
    {
        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket:
                _tokenDebt = SaturatingAdd(_tokenDebt, 1);
                break;
            case AdmissionRateStateKind.FixedWindow:
                _fixedConsumed = SaturatingAdd(_fixedConsumed, 1);
                break;
            case AdmissionRateStateKind.SlidingWindow:
                _slidingSegments[_slidingCurrentSegment] = SaturatingAdd(
                    _slidingSegments[_slidingCurrentSegment], 1);
                _slidingOwnTotal = SaturatingAdd(_slidingOwnTotal, 1);
                break;
            default:
                throw new InvalidOperationException("Unsupported admission rate state kind.");
        }
    }

    private void RecordLegacyGrantLocked(long now, long sourceExpiry)
    {
        _transitionDebt = SaturatingAdd(_transitionDebt, 1);
        var targetExpiry = SaturatingAdd(now, GetBarrierHorizonTimestampTicks(1));
        _transitionDebtExpiry = Math.Max(
            _transitionDebtExpiry,
            Math.Max(sourceExpiry, targetExpiry));
        _latestGrantTimestamp = Math.Max(_latestGrantTimestamp, now);
    }

    private bool CanGrantLocked()
        => GetBurdenLocked() < _definition.Limit;

    private long GetBurdenLocked()
        => SaturatingAdd(_transitionDebt, GetOwnBurdenLocked());

    private long GetOwnBurdenLocked()
        => _definition.Kind switch
        {
            AdmissionRateStateKind.TokenBucket => _tokenDebt,
            AdmissionRateStateKind.FixedWindow => _fixedConsumed,
            AdmissionRateStateKind.SlidingWindow => _slidingOwnTotal,
            _ => long.MaxValue
        };

    private long GetDebtExpiryLocked(long now)
    {
        if (GetBurdenLocked() == 0)
            return now;

        var expiry = _transitionDebt == 0 ? now : _transitionDebtExpiry;
        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket when _tokenDebt != 0:
                expiry = Math.Max(expiry, GetTokenDebtExpiryLocked());
                break;
            case AdmissionRateStateKind.FixedWindow when _fixedConsumed != 0:
                expiry = Math.Max(
                    expiry,
                    SaturatingAdd(_fixedWindowStart, GetWindowTimestampTicks()));
                break;
            case AdmissionRateStateKind.SlidingWindow when _slidingOwnTotal != 0:
                expiry = Math.Max(expiry, GetSlidingOwnDebtExpiryLocked());
                break;
        }
        return expiry;
    }

    private long GetTokenDebtExpiryLocked()
    {
        var periods = DivideRoundUp(_tokenDebt, _definition.Secondary);
        return SaturatingAdd(
            _tokenAnchor,
            SaturatingMultiply(periods, GetPeriodTimestampTicks()));
    }

    private long GetSlidingOwnDebtExpiryLocked()
        => SaturatingAdd(
            _slidingSegmentStart,
            SaturatingMultiply(_definition.Segments, GetSlidingSegmentTimestampTicks()));

    private void AdvanceLocked(long now)
    {
        if (_transitionDebt != 0 && now >= _transitionDebtExpiry)
        {
            _transitionDebt = 0;
            _transitionDebtExpiry = 0;
        }

        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket:
                AdvanceTokenBucketLocked(now);
                break;
            case AdmissionRateStateKind.FixedWindow:
                AdvanceFixedWindowLocked(now);
                break;
            case AdmissionRateStateKind.SlidingWindow:
                AdvanceSlidingWindowLocked(now);
                break;
        }
    }

    private void AdvanceTokenBucketLocked(long now)
    {
        if (_tokenDebt == 0)
        {
            _tokenAnchor = now;
            return;
        }

        var period = GetPeriodTimestampTicks();
        var elapsed = now - _tokenAnchor;
        if (elapsed < period)
            return;

        var periods = elapsed / period;
        var credit = SaturatingMultiply(periods, _definition.Secondary);
        _tokenDebt = Math.Max(0, _tokenDebt - credit);
        _tokenAnchor = SaturatingAdd(_tokenAnchor, SaturatingMultiply(periods, period));
    }

    private void AdvanceFixedWindowLocked(long now)
    {
        var window = GetWindowTimestampTicks();
        var elapsed = now - _fixedWindowStart;
        if (elapsed < window)
            return;

        var windows = elapsed / window;
        _fixedWindowStart = SaturatingAdd(
            _fixedWindowStart,
            SaturatingMultiply(windows, window));
        _fixedConsumed = 0;
    }

    private void AdvanceSlidingWindowLocked(long now)
    {
        var segment = GetSlidingSegmentTimestampTicks();
        var elapsed = now - _slidingSegmentStart;
        if (elapsed < segment)
            return;

        var steps = elapsed / segment;
        if (steps >= _definition.Segments)
        {
            Array.Clear(_slidingSegments);
            _slidingOwnTotal = 0;
            _slidingCurrentSegment = (_slidingCurrentSegment + (int)(steps % _definition.Segments)) %
                                     _definition.Segments;
            _slidingSegmentStart = SaturatingAdd(
                _slidingSegmentStart,
                SaturatingMultiply(steps, segment));
            return;
        }

        for (var step = 0L; step < steps; step++)
        {
            _slidingCurrentSegment = (_slidingCurrentSegment + 1) % _definition.Segments;
            var expired = _slidingSegments[_slidingCurrentSegment];
            if (expired != 0)
            {
                _slidingOwnTotal -= expired;
                _slidingSegments[_slidingCurrentSegment] = 0;
            }
            _slidingSegmentStart = SaturatingAdd(_slidingSegmentStart, segment);
        }
    }

    private long GetNextAvailabilityTimestampLocked(long now)
    {
        if (CanGrantLocked())
            return now;

        var next = _transitionDebt == 0 ? long.MaxValue : _transitionDebtExpiry;
        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket when _tokenDebt != 0:
                next = Math.Min(next, GetNextTokenOwnAvailabilityLocked());
                break;
            case AdmissionRateStateKind.FixedWindow when _fixedConsumed != 0:
                next = Math.Min(
                    next,
                    SaturatingAdd(_fixedWindowStart, GetWindowTimestampTicks()));
                break;
            case AdmissionRateStateKind.SlidingWindow when _slidingOwnTotal != 0:
                next = Math.Min(next, GetNextSlidingOwnAvailabilityLocked());
                break;
        }
        return next;
    }

    private long GetNextTokenOwnAvailabilityLocked()
    {
        var requiredReduction = GetBurdenLocked() - _definition.Limit + 1;
        if (requiredReduction <= 0)
            return _timeProvider.GetTimestamp();
        if (requiredReduction > _tokenDebt)
            return long.MaxValue;

        var periods = DivideRoundUp(requiredReduction, _definition.Secondary);
        return SaturatingAdd(
            _tokenAnchor,
            SaturatingMultiply(periods, GetPeriodTimestampTicks()));
    }

    private long GetNextSlidingOwnAvailabilityLocked()
    {
        var segment = GetSlidingSegmentTimestampTicks();
        for (var offset = 1; offset <= _definition.Segments; offset++)
        {
            var index = (_slidingCurrentSegment + offset) % _definition.Segments;
            if (_slidingSegments[index] == 0)
                continue;
            return SaturatingAdd(
                _slidingSegmentStart,
                SaturatingMultiply(offset, segment));
        }
        return long.MaxValue;
    }

    private RateWaiter? GrantWaitersLocked(long now)
    {
        AdvanceLocked(now);
        RateWaiter? grantedHead = null;
        RateWaiter? grantedTail = null;
        while (_waiterHead is not null && CanGrantLocked())
        {
            var waiter = DequeueLocked();
            RecordGrantLocked(now);
            if (grantedTail is null)
                grantedHead = waiter;
            else
                grantedTail.Next = waiter;
            grantedTail = waiter;
        }
        ScheduleTimerLocked(now);
        return grantedHead;
    }

    private void ScheduleTimerLocked(long now)
    {
        if (_disposed != 0 || _waiterHead is null)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        AdvanceLocked(now);
        if (CanGrantLocked())
            return;

        var next = GetNextAvailabilityTimestampLocked(now);
        if (next == long.MaxValue)
            return;
        var due = TimestampDeltaToTimeSpan(Math.Max(1, next - now));
        _timer ??= _timeProvider.CreateTimer(
            static state => ((AdmissionDynamicRateState)state!).OnTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        RateWaiter? granted;
        lock (Lineage.Gate)
        {
            if (_disposed != 0)
                return;
            granted = GrantWaitersLocked(_timeProvider.GetTimestamp());
        }
        CompleteGranted(granted);
    }

    private void CancelWaiter(RateWaiter waiter)
    {
        var removed = false;
        lock (Lineage.Gate)
        {
            removed = RemoveLocked(waiter);
            if (removed)
                ScheduleTimerLocked(_timeProvider.GetTimestamp());
        }
        if (removed)
            waiter.CompleteCanceled();
    }

    private void EnqueueLocked(RateWaiter waiter)
    {
        waiter.IsQueued = true;
        waiter.Previous = _waiterTail;
        if (_waiterTail is null)
            _waiterHead = waiter;
        else
            _waiterTail.Next = waiter;
        _waiterTail = waiter;
        _waitingCount++;
    }

    private RateWaiter DequeueLocked()
    {
        var waiter = _waiterHead ??
            throw new InvalidOperationException("Admission rate waiter queue was unexpectedly empty.");
        var next = waiter.Next;
        _waiterHead = next;
        if (next is null)
            _waiterTail = null;
        else
            next.Previous = null;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.IsQueued = false;
        _waitingCount--;
        return waiter;
    }

    private bool RemoveLocked(RateWaiter waiter)
    {
        if (!waiter.IsQueued)
            return false;
        var previous = waiter.Previous;
        var next = waiter.Next;
        if (previous is null)
            _waiterHead = next;
        else
            previous.Next = next;
        if (next is null)
            _waiterTail = previous;
        else
            next.Previous = previous;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.IsQueued = false;
        _waitingCount--;
        return true;
    }

    private RateWaiter? DetachAllLocked()
    {
        var head = _waiterHead;
        _waiterHead = null;
        _waiterTail = null;
        _waitingCount = 0;
        for (var waiter = head; waiter is not null; waiter = waiter.Next)
        {
            waiter.Previous = null;
            waiter.IsQueued = false;
        }
        return head;
    }

    private static void CompleteGranted(RateWaiter? waiter)
    {
        while (waiter is not null)
        {
            var next = waiter.Next;
            waiter.Next = null;
            waiter.CompleteGranted();
            waiter = next;
        }
    }

    private static void CompleteFailed(RateWaiter? waiter)
    {
        while (waiter is not null)
        {
            var next = waiter.Next;
            waiter.Next = null;
            waiter.CompleteFailed();
            waiter = next;
        }
    }

    public void Dispose()
    {
        RateWaiter? failed;
        ITimer? timer;
        lock (Lineage.Gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            failed = DetachAllLocked();
            timer = _timer;
            _timer = null;
            Lineage.DetachIfCurrentLocked(this);
        }
        timer?.Dispose();
        CompleteFailed(failed);
    }

    private long GetBarrierHorizonTimestampTicks(long burden)
    {
        if (burden <= 0)
            return 0;

        return _definition.Kind switch
        {
            AdmissionRateStateKind.TokenBucket => SaturatingMultiply(
                DivideRoundUp(burden, _definition.Secondary),
                GetPeriodTimestampTicks()),
            AdmissionRateStateKind.FixedWindow => GetWindowTimestampTicks(),
            AdmissionRateStateKind.SlidingWindow => GetWindowTimestampTicks(),
            _ => long.MaxValue
        };
    }

    private long GetPeriodTimestampTicks()
        => ToTimestampTicks(_definition.PeriodTicks);

    private long GetWindowTimestampTicks()
        => ToTimestampTicks(_definition.PeriodTicks);

    private long GetSlidingSegmentTimestampTicks()
        => Math.Max(1, GetWindowTimestampTicks() / _definition.Segments);

    private long ToTimestampTicks(long timeSpanTicks)
    {
        var scaled = (decimal)timeSpanTicks * _timeProvider.TimestampFrequency /
                     TimeSpan.TicksPerSecond;
        if (scaled >= long.MaxValue)
            return long.MaxValue;
        return Math.Max(1, (long)Math.Ceiling(scaled));
    }

    private TimeSpan TimestampDeltaToTimeSpan(long timestampTicks)
    {
        var scaled = (decimal)timestampTicks * TimeSpan.TicksPerSecond /
                     _timeProvider.TimestampFrequency;
        if (scaled >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(scaled)));
    }

    private static long DivideRoundUp(long value, long divisor)
        => value <= 0 ? 0 : (value - 1) / divisor + 1;

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static long SaturatingMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
            return 0;
        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static void ValidatePermitCount(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                "Admission rate limiters acquire exactly one permit.");
        }
    }

    private sealed class RateWaiter(
        AdmissionDynamicRateState owner,
        CancellationToken cancellationToken)
        : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal AdmissionDynamicRateState Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal RateWaiter? Previous { get; set; }
        internal RateWaiter? Next { get; set; }
        internal bool IsQueued { get; set; }

        internal void SetRegistration(CancellationTokenRegistration registration)
        {
            _registration = registration;
            if (Volatile.Read(ref _completed) != 0)
                registration.Dispose();
        }

        internal void CompleteGranted()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            TrySetResult(AcquiredLease.Instance);
        }

        internal void CompleteCanceled()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            TrySetCanceled(CancellationToken);
        }

        internal void CompleteFailed()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            TrySetResult(FailedLease.Instance);
        }
    }

    private sealed class AcquiredLease : RateLimitLease
    {
        internal static AcquiredLease Instance { get; } = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class FailedLease : RateLimitLease
    {
        internal static FailedLease Instance { get; } = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
