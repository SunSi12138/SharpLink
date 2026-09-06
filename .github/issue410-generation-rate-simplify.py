from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


state = ROOT / "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs"
state.write_text(r'''using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Generation-scoped TokenBucket / SlidingWindow state. A changed definition gets a fresh state;
/// old captured programs keep their own state until they drain. FixedWindow uses its stable counter.
/// </summary>
internal sealed class AdmissionDynamicRateState : IDisposable, IAdmissionRateWaiterOwner
{
    private readonly Lock _gate = new();
    private readonly AdmissionRateStateDefinition _definition;
    private readonly TimeProvider _timeProvider;
    private readonly long[] _slidingSegments;
    private AdmissionRateWaitQueue _waiters;
    private ITimer? _timer;
    private long _tokenDebt;
    private long _tokenAnchor;
    private long _slidingOwnTotal;
    private int _slidingCurrentSegment;
    private long _slidingSegmentStart;
    private int _disposed;

    internal AdmissionDynamicRateState(
        AdmissionRateStateDefinition definition,
        TimeProvider timeProvider)
    {
        if (definition.Kind is AdmissionRateStateKind.None or AdmissionRateStateKind.FixedWindow)
        {
            throw new InvalidOperationException(
                "Generation-scoped rate state supports TokenBucket or SlidingWindow only.");
        }
        _definition = definition;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _slidingSegments = definition.Kind == AdmissionRateStateKind.SlidingWindow
            ? new long[definition.Segments]
            : [];

        var now = _timeProvider.GetTimestamp();
        _tokenAnchor = now;
        _slidingSegmentStart = now;
    }

    internal AdmissionRateStateDefinition Definition => _definition;
    internal object Identity => this;

    internal int WaitingCount
    {
        get { lock (_gate) return _waiters.Count; }
    }

    // Keep the existing diagnostic surface while the experiment measures which migration-only tests
    // become obsolete. These report this generation's own burden/expiry, never predecessor debt.
    internal long TransitionDebtForDiagnostics
    {
        get
        {
            lock (_gate)
            {
                AdvanceLocked(_timeProvider.GetTimestamp());
                return GetOwnBurdenLocked();
            }
        }
    }

    internal long TransitionBarrierExpiryForDiagnostics
    {
        get
        {
            lock (_gate)
            {
                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                return GetOwnDebtExpiryLocked(now);
            }
        }
    }

    internal RateLimitLease AttemptAcquire(int permitCount)
    {
        ValidatePermitCount(permitCount);
        lock (_gate)
        {
            if (_disposed != 0)
                return AdmissionRateLeases.Failed;

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waiters.Count != 0 || !CanGrantLocked())
                return AdmissionRateLeases.Failed;

            RecordGrantLocked();
            return AdmissionRateLeases.Acquired;
        }
    }

    internal ValueTask<RateLimitLease> AcquireAsync(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        AdmissionRateWaiter waiter;
        lock (_gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Failed);

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waiters.Count == 0 && CanGrantLocked())
            {
                RecordGrantLocked();
                return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Acquired);
            }

            waiter = new AdmissionRateWaiter(this, cancellationToken);
            _waiters.Enqueue(waiter);
            ScheduleTimerLocked(now);
        }

        waiter.RegisterCancellation();
        return new ValueTask<RateLimitLease>(waiter.Task);
    }

    private void RecordGrantLocked()
    {
        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket:
                _tokenDebt = SaturatingAdd(_tokenDebt, 1);
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

    private bool CanGrantLocked() => GetOwnBurdenLocked() < _definition.Limit;

    private long GetOwnBurdenLocked()
        => _definition.Kind switch
        {
            AdmissionRateStateKind.TokenBucket => _tokenDebt,
            AdmissionRateStateKind.SlidingWindow => _slidingOwnTotal,
            _ => long.MaxValue
        };

    private long GetOwnDebtExpiryLocked(long now)
    {
        if (GetOwnBurdenLocked() == 0)
            return now;
        return _definition.Kind switch
        {
            AdmissionRateStateKind.TokenBucket => SaturatingAdd(
                _tokenAnchor,
                SaturatingMultiply(
                    DivideRoundUp(_tokenDebt, _definition.Secondary),
                    GetPeriodTimestampTicks())),
            AdmissionRateStateKind.SlidingWindow => GetSlidingOwnDebtExpiryLocked(),
            _ => long.MaxValue
        };
    }

    private long GetSlidingOwnDebtExpiryLocked()
        => SaturatingAdd(
            _slidingSegmentStart,
            SaturatingMultiply(_definition.Segments, GetSlidingSegmentTimestampTicks()));

    private void AdvanceLocked(long now)
    {
        if (_definition.Kind == AdmissionRateStateKind.TokenBucket)
            AdvanceTokenBucketLocked(now);
        else
            AdvanceSlidingWindowLocked(now);
    }

    private void AdvanceTokenBucketLocked(long now)
    {
        var period = GetPeriodTimestampTicks();
        var elapsed = now - _tokenAnchor;
        if (elapsed < period)
            return;

        var periods = elapsed / period;
        var credit = SaturatingMultiply(periods, _definition.Secondary);
        _tokenDebt = Math.Max(0, _tokenDebt - credit);
        _tokenAnchor = SaturatingAdd(_tokenAnchor, SaturatingMultiply(periods, period));
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
        return _definition.Kind switch
        {
            AdmissionRateStateKind.TokenBucket => GetNextTokenAvailabilityLocked(),
            AdmissionRateStateKind.SlidingWindow => GetNextSlidingOwnAvailabilityLocked(),
            _ => long.MaxValue
        };
    }

    private long GetNextTokenAvailabilityLocked()
    {
        var requiredReduction = _tokenDebt - _definition.Limit + 1;
        if (requiredReduction <= 0)
            return _timeProvider.GetTimestamp();
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

    private AdmissionRateWaiter? GrantWaitersLocked(long now)
    {
        AdvanceLocked(now);
        AdmissionRateWaiter? grantedHead = null;
        AdmissionRateWaiter? grantedTail = null;
        while (!_waiters.IsEmpty && CanGrantLocked())
        {
            var waiter = _waiters.Dequeue();
            RecordGrantLocked();
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
        if (_disposed != 0 || _waiters.IsEmpty)
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
        AdmissionRateWaiter? granted;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            granted = GrantWaitersLocked(_timeProvider.GetTimestamp());
        }
        AdmissionRateWaitQueue.CompleteGranted(granted);
    }

    void IAdmissionRateWaiterOwner.CancelRateWaiter(AdmissionRateWaiter waiter) => CancelWaiter(waiter);

    private void CancelWaiter(AdmissionRateWaiter waiter)
    {
        var removed = false;
        lock (_gate)
        {
            removed = _waiters.Remove(waiter);
            if (removed)
                ScheduleTimerLocked(_timeProvider.GetTimestamp());
        }
        if (removed)
            waiter.CompleteCanceled();
    }

    public void Dispose()
    {
        AdmissionRateWaiter? failed;
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            failed = _waiters.DetachAll();
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        AdmissionRateWaitQueue.CompleteFailed(failed);
    }

    private long GetPeriodTimestampTicks()
        => ToTimestampTicks(_definition.PeriodTicks);

    private long GetWindowTimestampTicks()
        => ToTimestampTicks(_definition.PeriodTicks);

    private long GetSlidingSegmentTimestampTicks()
        => Math.Max(1, DivideRoundUp(GetWindowTimestampTicks(), _definition.Segments));

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
}
''')

# Token/Sliding successors are independent generation-scoped states; Fixed still shares its Counter.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
replace_once(
    path,
    "    internal object LineageIdentity => _fixedCounter ?? (object)_state!.Lineage;",
    "    internal object LineageIdentity => _fixedCounter ?? (object)_state!;",
)
replace_once(
    path,
    """        var state = new AdmissionDynamicRateState(\n            definition,\n            timeProvider,\n            transitionSource?._state?.Lineage);\n""",
    """        var state = new AdmissionDynamicRateState(definition, timeProvider);\n""",
)
replace_once(
    path,
    """        if (target?._state is not null && ReferenceEquals(_state!.Lineage, target._state.Lineage))\n            _state.CommitTransitionTo(target._state);\n        else\n            _state!.CommitTransitionTo(null);\n""",
    """        // TokenBucket / SlidingWindow updates are generation-scoped and need no state commit.\n""",
)

# Migration anchor detection remains meaningful only for Fixed wrappers sharing the same Counter.
path = "src/SharpLink.Server/Admission/AdmissionStateKernel.cs"
replace_once(
    path,
    "if (ReferenceEquals(pair.Value.State.Lineage, state.Lineage))",
    "if (ReferenceEquals(pair.Value.State.LineageIdentity, state.LineageIdentity))",
)

print("issue #410 generation-scoped Token/Sliding experiment staged")
