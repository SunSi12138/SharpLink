using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Immutable FixedWindow policy view over one stable logical counter. Synchronous attempts use the
/// policy captured by their AdmissionProgram. Rate waiters are a later admission attempt and follow
/// the latest published limit for the currently active window. Window changes activate only at the
/// next natural boundary of the shared counter.
/// </summary>
internal sealed class DynamicFixedWindowRateLimiter : RateLimiter
{
    private readonly Counter _counter;
    private readonly long _sequence;
    private readonly int _permitLimit;
    private readonly long _windowTimestampTicks;
    private int _preActivationLimit;
    private long _activationBoundary;
    private int _committed;
    private int _published;
    private int _disposed;

    internal DynamicFixedWindowRateLimiter(
        int permitLimit,
        TimeSpan window,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        var provider = timeProvider ?? TimeProvider.System;
        _counter = new Counter(permitLimit, window, provider);
        _sequence = 1;
        _permitLimit = permitLimit;
        _windowTimestampTicks = _counter.ToTimestampTicks(window.Ticks);
        _preActivationLimit = permitLimit;
        _committed = 1;
        _published = 1;
    }

    private DynamicFixedWindowRateLimiter(
        Counter counter,
        long sequence,
        int permitLimit,
        long windowTimestampTicks)
    {
        _counter = counter;
        _sequence = sequence;
        _permitLimit = permitLimit;
        _windowTimestampTicks = windowTimestampTicks;
    }

    internal int PermitLimit => _permitLimit;

    internal TimeSpan Window => _counter.TimestampDeltaToTimeSpan(_windowTimestampTicks);

    internal int WaitingCount => _counter.WaitingCount;

    internal long ConsumedForTests => _counter.Consumed;

    internal int ActiveLimitForTests => _counter.ActiveLimit;

    internal int QueuedLimitForTests => _counter.QueuedLimit;

    internal TimeSpan ActiveWindowForTests => _counter.ActiveWindow;

    internal bool HasPendingWindowForTests => _counter.HasPendingWindow;

    internal long CounterIdentityForTests => _counter.Identity;

    internal DynamicFixedWindowRateLimiter CreateSuccessor(int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        ThrowIfDisposed();
        return _counter.CreateSuccessor(permitLimit, window);
    }

    /// <summary>
    /// Finalizes a winning successor without mutating the live counter. The actual shared target is
    /// installed only after publication, so a losing candidate and the commit-before-pointer window
    /// cannot leak target state into requests still bound to the old publication.
    /// </summary>
    internal void CommitTransitionTo(DynamicFixedWindowRateLimiter target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        _counter.CommitTransition(this, target);
    }

    /// <summary>
    /// Installs this policy as the published target. Server publication calls this after the program
    /// pointer is visible. Acquisition also calls it as a lazy fallback for direct kernel tests and
    /// other internal publication paths.
    /// </summary>
    internal void OnPublished()
    {
        ThrowIfDisposed();
        _counter.Publish(this);
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);
        if (Volatile.Read(ref _disposed) != 0)
            return FailedLease.Instance;
        _counter.Publish(this);
        return _counter.AttemptAcquire(this);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);
        _counter.Publish(this);
        return _counter.AcquireAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _counter.ReleaseView();
    }

    private void FinalizeForCommit(int preActivationLimit, long activationBoundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preActivationLimit);
        if (Interlocked.Exchange(ref _committed, 1) != 0)
            throw new InvalidOperationException("Dynamic FixedWindow policy view was committed more than once.");
        _preActivationLimit = preActivationLimit;
        _activationBoundary = activationBoundary;
    }

    private void MarkPublishedLocked()
        => Volatile.Write(ref _published, 1);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DynamicFixedWindowRateLimiter));
    }

    private static void ValidatePermitCount(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                "Admission FixedWindow limiters acquire exactly one permit.");
        }
    }

    private sealed class Counter
    {
        private static long s_nextIdentity;

        private readonly Lock _gate = new();
        private readonly TimeProvider _timeProvider;
        private RateWaiter? _waiterHead;
        private RateWaiter? _waiterTail;
        private ITimer? _timer;
        private long _windowStart;
        private long _activeWindowTimestampTicks;
        private long _consumed;
        private long _nextSequence = 1;
        private long _retiredThroughSequence;
        private long _pendingSequence;
        private long _pendingBoundary;
        private long _pendingWindowTimestampTicks;
        private int _activeLimit;
        private int _queuedLimit;
        private int _pendingLimit;
        private int _waitingCount;
        private int _references = 1;
        private int _disposed;

        internal Counter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            _activeLimit = permitLimit;
            _queuedLimit = permitLimit;
            _activeWindowTimestampTicks = ToTimestampTicks(window.Ticks);
            _windowStart = _timeProvider.GetTimestamp();
            Identity = Interlocked.Increment(ref s_nextIdentity);
        }

        internal long Identity { get; }

        internal int WaitingCount
        {
            get { lock (_gate) return _waitingCount; }
        }

        internal long Consumed
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _consumed;
                }
            }
        }

        internal int ActiveLimit
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _activeLimit;
                }
            }
        }

        internal int QueuedLimit
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _queuedLimit;
                }
            }
        }

        internal TimeSpan ActiveWindow
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return TimestampDeltaToTimeSpan(_activeWindowTimestampTicks);
                }
            }
        }

        internal bool HasPendingWindow
        {
            get { lock (_gate) return _pendingSequence != 0; }
        }

        internal DynamicFixedWindowRateLimiter CreateSuccessor(int permitLimit, TimeSpan window)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var sequence = checked(++_nextSequence);
                var windowTicks = ToTimestampTicks(window.Ticks);
                _references = checked(_references + 1);
                return new DynamicFixedWindowRateLimiter(this, sequence, permitLimit, windowTicks);
            }
        }

        internal void CommitTransition(
            DynamicFixedWindowRateLimiter source,
            DynamicFixedWindowRateLimiter target)
        {
            if (!ReferenceEquals(source._counter, this) || !ReferenceEquals(target._counter, this))
                throw new InvalidOperationException("Dynamic FixedWindow transition crossed logical counters.");

            RateWaiter? granted;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var now = _timeProvider.GetTimestamp();
                granted = PublishLocked(source, now);
                AdvanceLocked(now);
                var sourceLimit = GetDirectLimitLocked(source);
                var boundary = SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
                target.FinalizeForCommit(sourceLimit, boundary);
            }
            CompleteGranted(granted);
        }

        internal void Publish(DynamicFixedWindowRateLimiter policy)
        {
            if (Volatile.Read(ref policy._published) != 0)
                return;

            RateWaiter? granted;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                granted = PublishLocked(policy, _timeProvider.GetTimestamp());
            }
            CompleteGranted(granted);
        }

        internal RateLimitLease AttemptAcquire(DynamicFixedWindowRateLimiter policy)
        {
            lock (_gate)
            {
                if (_disposed != 0)
                    return FailedLease.Instance;

                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                if (_waitingCount != 0 || _consumed >= GetDirectLimitLocked(policy))
                    return FailedLease.Instance;

                _consumed++;
                return AcquiredLease.Instance;
            }
        }

        internal ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

            RateWaiter waiter;
            lock (_gate)
            {
                if (_disposed != 0)
                    return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                if (_waitingCount == 0 && _consumed < _queuedLimit)
                {
                    _consumed++;
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

        internal void ReleaseView()
        {
            RateWaiter? failed = null;
            ITimer? timer = null;
            lock (_gate)
            {
                if (--_references < 0)
                    throw new InvalidOperationException("Dynamic FixedWindow view reference count underflowed.");
                if (_references != 0 || _disposed != 0)
                    return;

                _disposed = 1;
                failed = DetachAllLocked();
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
            CompleteFailed(failed);
        }

        internal long ToTimestampTicks(long timeSpanTicks)
        {
            var scaled = (decimal)timeSpanTicks * _timeProvider.TimestampFrequency /
                         TimeSpan.TicksPerSecond;
            if (scaled >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(1, (long)Math.Ceiling(scaled));
        }

        internal TimeSpan TimestampDeltaToTimeSpan(long timestampTicks)
        {
            var scaled = (decimal)timestampTicks * TimeSpan.TicksPerSecond /
                         _timeProvider.TimestampFrequency;
            if (scaled >= TimeSpan.MaxValue.Ticks)
                return TimeSpan.MaxValue;
            return TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(scaled)));
        }

        private RateWaiter? PublishLocked(DynamicFixedWindowRateLimiter policy, long now)
        {
            if (Volatile.Read(ref policy._published) != 0)
                return null;
            if (Volatile.Read(ref policy._committed) == 0)
                throw new InvalidOperationException("Uncommitted Dynamic FixedWindow policy became visible.");

            AdvanceLocked(now);
            if (policy._windowTimestampTicks == _activeWindowTimestampTicks)
            {
                ClearPendingLocked();
                _queuedLimit = policy._permitLimit;
            }
            else if (now < policy._activationBoundary)
            {
                _pendingSequence = policy._sequence;
                _pendingBoundary = policy._activationBoundary;
                _pendingLimit = policy._permitLimit;
                _pendingWindowTimestampTicks = policy._windowTimestampTicks;
                _queuedLimit = policy._preActivationLimit;
            }
            else
            {
                ActivateLatePublishedPolicyLocked(policy, now);
            }

            policy.MarkPublishedLocked();
            return GrantWaitersLocked(now);
        }

        private int GetDirectLimitLocked(DynamicFixedWindowRateLimiter policy)
        {
            if (!ReferenceEquals(policy._counter, this))
                throw new InvalidOperationException("Dynamic FixedWindow policy belongs to another counter.");
            if (policy._sequence <= _retiredThroughSequence)
                return _activeLimit;
            if (policy._windowTimestampTicks == _activeWindowTimestampTicks)
                return policy._permitLimit;
            if (Volatile.Read(ref policy._committed) == 0)
                throw new InvalidOperationException("Uncommitted Dynamic FixedWindow policy became visible.");
            return policy._preActivationLimit;
        }

        private void AdvanceLocked(long now)
        {
            if (_pendingSequence != 0 && now >= _pendingBoundary)
            {
                _windowStart = _pendingBoundary;
                _consumed = 0;
                _activeLimit = _pendingLimit;
                _queuedLimit = _pendingLimit;
                _activeWindowTimestampTicks = _pendingWindowTimestampTicks;
                _retiredThroughSequence = Math.Max(_retiredThroughSequence, _pendingSequence);
                ClearPendingLocked();
            }

            var boundary = SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
            if (now < boundary)
                return;

            var elapsed = now - _windowStart;
            var windows = elapsed / _activeWindowTimestampTicks;
            _windowStart = SaturatingAdd(
                _windowStart,
                SaturatingMultiply(windows, _activeWindowTimestampTicks));
            _consumed = 0;
        }

        private void ActivateLatePublishedPolicyLocked(
            DynamicFixedWindowRateLimiter policy,
            long now)
        {
            var activationBoundary = policy._activationBoundary;
            _activeLimit = policy._permitLimit;
            _queuedLimit = policy._permitLimit;
            _activeWindowTimestampTicks = policy._windowTimestampTicks;
            _retiredThroughSequence = Math.Max(_retiredThroughSequence, policy._sequence);
            ClearPendingLocked();

            // Normally publication happens immediately after commit and this branch is reached only
            // when the natural boundary raced publication. If old captured work already advanced the
            // counter past that boundary, preserve its consumed count rather than forgiving quota.
            if (_windowStart < activationBoundary)
                _windowStart = activationBoundary;
            if (_windowStart > activationBoundary ||
                now >= SaturatingAdd(_windowStart, _activeWindowTimestampTicks))
            {
                _windowStart = now;
            }
        }

        private RateWaiter? GrantWaitersLocked(long now)
        {
            AdvanceLocked(now);
            RateWaiter? grantedHead = null;
            RateWaiter? grantedTail = null;
            while (_waiterHead is not null && _consumed < _queuedLimit)
            {
                var waiter = DequeueLocked();
                _consumed++;
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
            if (_consumed < _queuedLimit)
                return;

            var next = _pendingSequence != 0
                ? _pendingBoundary
                : SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
            if (next == long.MaxValue)
                return;
            var due = TimestampDeltaToTimeSpan(Math.Max(1, next - now));
            _timer ??= _timeProvider.CreateTimer(
                static state => ((Counter)state!).OnTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }

        private void OnTimer()
        {
            RateWaiter? granted;
            lock (_gate)
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
            lock (_gate)
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
                throw new InvalidOperationException("Dynamic FixedWindow waiter queue was unexpectedly empty.");
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

        private void ClearPendingLocked()
        {
            _pendingSequence = 0;
            _pendingBoundary = 0;
            _pendingLimit = 0;
            _pendingWindowTimestampTicks = 0;
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(DynamicFixedWindowRateLimiter));
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
    }

    private sealed class RateWaiter(
        Counter owner,
        CancellationToken cancellationToken)
        : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal Counter Owner { get; } = owner;
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
