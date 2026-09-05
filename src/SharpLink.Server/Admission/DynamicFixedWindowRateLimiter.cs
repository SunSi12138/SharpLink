using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal enum DynamicFixedWindowActivationMode : byte
{
    Immediate,
    NextWindowBoundary
}

/// <summary>
/// Prototype stable logical FixedWindow ledger. Supported updates mutate one authoritative
/// accounting state instead of creating a successor rate generation and translating window history.
/// </summary>
internal sealed class DynamicFixedWindowRateLimiter : RateLimiter
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private RateWaiter? _waiterHead;
    private RateWaiter? _waiterTail;
    private ITimer? _timer;
    private long _windowStart;
    private long _windowTimestampTicks;
    private long _consumed;
    private int _permitLimit;
    private long _pendingWindowTimestampTicks;
    private int _pendingPermitLimit;
    private bool _hasPending;
    private int _waitingCount;
    private int _disposed;

    internal DynamicFixedWindowRateLimiter(
        int permitLimit,
        TimeSpan window,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _permitLimit = permitLimit;
        _windowTimestampTicks = ToTimestampTicks(window.Ticks);
        _windowStart = _timeProvider.GetTimestamp();
    }

    internal int CurrentPermitLimit
    {
        get
        {
            lock (_gate)
                return _permitLimit;
        }
    }

    internal TimeSpan CurrentWindow
    {
        get
        {
            lock (_gate)
                return TimestampDeltaToTimeSpan(_windowTimestampTicks);
        }
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

    internal int WaitingCount
    {
        get
        {
            lock (_gate)
                return _waitingCount;
        }
    }

    internal bool HasPendingUpdate
    {
        get
        {
            lock (_gate)
                return _hasPending;
        }
    }

    internal int PendingPermitLimit
    {
        get
        {
            lock (_gate)
                return _hasPending ? _pendingPermitLimit : 0;
        }
    }

    internal TimeSpan? PendingWindow
    {
        get
        {
            lock (_gate)
                return _hasPending
                    ? TimestampDeltaToTimeSpan(_pendingWindowTimestampTicks)
                    : null;
        }
    }

    internal void Update(
        int permitLimit,
        TimeSpan window,
        DynamicFixedWindowActivationMode activationMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        var targetWindow = ToTimestampTicks(window.Ticks);
        RateWaiter? granted = null;
        lock (_gate)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(DynamicFixedWindowRateLimiter));

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            switch (activationMode)
            {
                case DynamicFixedWindowActivationMode.Immediate:
                    var windowChanged = targetWindow != _windowTimestampTicks;
                    _permitLimit = permitLimit;
                    _windowTimestampTicks = targetWindow;
                    _hasPending = false;
                    _pendingPermitLimit = 0;
                    _pendingWindowTimestampTicks = 0;
                    if (windowChanged)
                    {
                        // Start a new policy epoch at publication, but never forgive consumption
                        // already charged to the authoritative logical FixedWindow ledger.
                        _windowStart = now;
                    }
                    granted = GrantWaitersLocked(now);
                    break;
                case DynamicFixedWindowActivationMode.NextWindowBoundary:
                    _pendingPermitLimit = permitLimit;
                    _pendingWindowTimestampTicks = targetWindow;
                    _hasPending = true;
                    ScheduleTimerLocked(now);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(activationMode));
            }
        }
        CompleteGranted(granted);
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);
        lock (_gate)
        {
            if (_disposed != 0)
                return FailedLease.Instance;

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waitingCount != 0 || _consumed >= _permitLimit)
                return FailedLease.Instance;

            _consumed++;
            return AcquiredLease.Instance;
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        RateWaiter waiter;
        lock (_gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

            var now = _timeProvider.GetTimestamp();
            AdvanceLocked(now);
            if (_waitingCount == 0 && _consumed < _permitLimit)
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

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        RateWaiter? failed;
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            failed = DetachAllLocked();
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        CompleteFailed(failed);
    }

    private void AdvanceLocked(long now)
    {
        var boundary = SaturatingAdd(_windowStart, _windowTimestampTicks);
        if (now < boundary)
            return;

        _windowStart = boundary;
        _consumed = 0;
        if (_hasPending)
        {
            _permitLimit = _pendingPermitLimit;
            _windowTimestampTicks = _pendingWindowTimestampTicks;
            _hasPending = false;
            _pendingPermitLimit = 0;
            _pendingWindowTimestampTicks = 0;
        }

        var elapsed = now - _windowStart;
        if (elapsed < _windowTimestampTicks)
            return;

        var windows = elapsed / _windowTimestampTicks;
        _windowStart = SaturatingAdd(
            _windowStart,
            SaturatingMultiply(windows, _windowTimestampTicks));
        _consumed = 0;
    }

    private RateWaiter? GrantWaitersLocked(long now)
    {
        AdvanceLocked(now);
        RateWaiter? grantedHead = null;
        RateWaiter? grantedTail = null;
        while (_waiterHead is not null && _consumed < _permitLimit)
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
        if (_consumed < _permitLimit)
            return;

        var next = SaturatingAdd(_windowStart, _windowTimestampTicks);
        if (next == long.MaxValue)
            return;
        var due = TimestampDeltaToTimeSpan(Math.Max(1, next - now));
        _timer ??= _timeProvider.CreateTimer(
            static state => ((DynamicFixedWindowRateLimiter)state!).OnTimer(),
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
                "Dynamic FixedWindow limiter acquires exactly one permit.");
        }
    }

    private sealed class RateWaiter(
        DynamicFixedWindowRateLimiter owner,
        CancellationToken cancellationToken)
        : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal DynamicFixedWindowRateLimiter Owner { get; } = owner;
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
