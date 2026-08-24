using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Stable concurrency state whose target may be changed without replacing active holders or queued
/// waiters. The state is shared by every overlapping program generation that binds the same logical
/// concurrency component.
/// </summary>
internal sealed class ResizableConcurrencyState : RateLimiter
{
    private readonly Lock _gate = new();
    private readonly AdmissionStateKernel? _targetVersionOwner;
    private Waiter? _waiterHead;
    private Waiter? _waiterTail;
    private int _waitingCount;
    private int _permitLimit;
    private int _active;
    private int _disposed;

    internal ResizableConcurrencyState(
        int permitLimit,
        AdmissionStateKernel? targetVersionOwner = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        _permitLimit = permitLimit;
        _targetVersionOwner = targetVersionOwner;
    }

    internal int PermitLimit
    {
        get
        {
            lock (_gate)
                return _permitLimit;
        }
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate)
                return _active;
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

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);
        var versionOwner = _targetVersionOwner;
        if (versionOwner is null)
            return AttemptAcquireStableCore();

        while (true)
        {
            // Do not begin a new immediate concurrency decision while a multi-state target commit
            // is physically between resizes. A permit acquired just before an update remains valid
            // by the no-preemption rule; only a failed decision that overlapped the commit retries.
            var version = versionOwner.ReadStableConcurrencyTargetVersion();
            var lease = AttemptAcquireStableCore();
            if (versionOwner.IsConcurrencyTargetVersionCurrent(version))
                return lease;

            if (lease.IsAcquired)
            {
                // The permit itself is a legitimate in-flight holder and survives target shrink.
                // Keep it, but do not expose it to the caller until the complete target set and
                // publication pointer are stable again.
                versionOwner.ReadStableConcurrencyTargetVersion();
                return lease;
            }

            lease.Dispose();
        }
    }

    private RateLimitLease AttemptAcquireStableCore()
    {
        // Match the BCL ConcurrencyLimiter hot-path shape: obvious exhaustion does not need the
        // state lock. A stale permissive read only falls through to the locked recheck; a failure
        // linearizes while active is at or above the observed target.
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _active) >= Volatile.Read(ref _permitLimit))
        {
            return FailedLease.Instance;
        }

        lock (_gate)
        {
            if (_disposed != 0)
                return FailedLease.Instance;

            // Do not let a new immediate caller barge ahead of an already queued Request.
            if (_waitingCount != 0 || _active >= _permitLimit)
                return FailedLease.Instance;

            _active++;
            return new ConcurrencyLease(this);
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        var versionOwner = _targetVersionOwner;
        if (versionOwner is null)
            return AcquireAsyncStableCore(cancellationToken);
        return AcquireVersionAwareAsync(versionOwner, cancellationToken);
    }

    private async ValueTask<RateLimitLease> AcquireVersionAwareAsync(
        AdmissionStateKernel versionOwner,
        CancellationToken cancellationToken)
    {
        // Queue residency has already been authorized by the outer kernel queue bound. Waiting for
        // an in-progress target commit here neither creates a second queue authority nor changes
        // FIFO order: an already queued waiter keeps its place and an acquired permit is retained.
        var version = versionOwner.ReadStableConcurrencyTargetVersion();
        var lease = await AcquireAsyncStableCore(cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired || versionOwner.IsConcurrencyTargetVersionCurrent(version))
            return lease;

        versionOwner.ReadStableConcurrencyTargetVersion();
        return lease;
    }

    private ValueTask<RateLimitLease> AcquireAsyncStableCore(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        Waiter waiter;
        lock (_gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

            if (_waitingCount == 0 && _active < _permitLimit)
            {
                _active++;
                return ValueTask.FromResult<RateLimitLease>(new ConcurrencyLease(this));
            }

            waiter = new Waiter(this, cancellationToken);
            EnqueueWaiterLocked(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.UnsafeRegister(
                static state => ((Waiter)state!).Owner.CancelWaiter((Waiter)state!),
                waiter);
            waiter.SetRegistration(registration);
        }
        return new ValueTask<RateLimitLease>(waiter.Task);
    }

    /// <summary>
    /// Commits a prevalidated target. Existing holders remain valid. Increasing capacity grants the
    /// oldest eligible queued Requests immediately; shrinking waits for natural releases.
    /// </summary>
    internal void Resize(int permitLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        Waiter? granted;
        lock (_gate)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ResizableConcurrencyState));
            _permitLimit = permitLimit;
            granted = GrantWaitersLocked();
        }
        CompleteGranted(granted);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        Waiter? failed;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            failed = DetachAllWaitersLocked();
        }

        CompleteFailed(failed);
    }

    private void ReleasePermit()
    {
        Waiter? granted = null;
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException("Admission concurrency permit count underflowed.");
            _active--;
            if (_disposed == 0)
                granted = GrantWaitersLocked();
        }
        CompleteGranted(granted);
    }

    private Waiter? GrantWaitersLocked()
    {
        Waiter? grantedHead = null;
        Waiter? grantedTail = null;
        while (_active < _permitLimit && _waiterHead is not null)
        {
            var waiter = DequeueWaiterLocked();
            _active++;
            if (grantedTail is null)
                grantedHead = waiter;
            else
                grantedTail.Next = waiter;
            grantedTail = waiter;
        }
        return grantedHead;
    }

    private void CancelWaiter(Waiter waiter)
    {
        var removed = false;
        lock (_gate)
            removed = RemoveWaiterLocked(waiter);
        if (removed)
            waiter.CompleteCanceled();
    }

    private void EnqueueWaiterLocked(Waiter waiter)
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

    private Waiter DequeueWaiterLocked()
    {
        var waiter = _waiterHead ??
            throw new InvalidOperationException("Admission concurrency waiter queue was unexpectedly empty.");
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

    private bool RemoveWaiterLocked(Waiter waiter)
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

    private Waiter? DetachAllWaitersLocked()
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

    private void CompleteGranted(Waiter? granted)
    {
        while (granted is not null)
        {
            var next = granted.Next;
            granted.Next = null;
            granted.CompleteGranted(this);
            granted = next;
        }
    }

    private static void CompleteFailed(Waiter? failed)
    {
        while (failed is not null)
        {
            var next = failed.Next;
            failed.Next = null;
            failed.CompleteFailed();
            failed = next;
        }
    }

    private static void ValidatePermitCount(int permitCount)
    {
        if (permitCount != 1)
            throw new ArgumentOutOfRangeException(nameof(permitCount), "Admission limiters acquire exactly one permit.");
    }

    private sealed class Waiter(
        ResizableConcurrencyState owner,
        CancellationToken cancellationToken)
        : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal ResizableConcurrencyState Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal Waiter? Previous { get; set; }
        internal Waiter? Next { get; set; }
        internal bool IsQueued { get; set; }

        internal void SetRegistration(CancellationTokenRegistration registration)
        {
            _registration = registration;
            if (Volatile.Read(ref _completed) != 0)
                registration.Dispose();
        }

        internal void CompleteGranted(ResizableConcurrencyState state)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            TrySetResult(new ConcurrencyLease(state));
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

    private sealed class ConcurrencyLease(ResizableConcurrencyState owner) : RateLimitLease
    {
        private ResizableConcurrencyState? _owner = owner;

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
            => Interlocked.Exchange(ref _owner, null)?.ReleasePermit();
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

/// <summary>Stable immutable-configuration rate state. Its BCL waiter capacity is fixed at the
/// maximum representable outer call bound; actual residency is authorized only by the kernel queue
/// reservation made before Admission calls AcquireAsync.</summary>
internal sealed class AdmissionRateState : RateLimiter
{
    private const int InnerQueueLimit = int.MaxValue;
    private readonly RateLimiter _limiter;

    private AdmissionRateState(RateLimiter limiter, AdmissionRateStateDefinition definition)
    {
        _limiter = limiter;
        Definition = definition;
    }

    internal AdmissionRateStateDefinition Definition { get; }

    internal static AdmissionRateState Create(SharpLinkAdmissionRuleOptions options)
    {
        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        RateLimiter limiter = options.RateLimit switch
        {
            SharpLinkTokenBucketLimitOptions tokenBucket => new TokenBucketRateLimiter(
                new TokenBucketRateLimiterOptions
                {
                    TokenLimit = tokenBucket.TokenLimit,
                    TokensPerPeriod = tokenBucket.TokensPerPeriod,
                    ReplenishmentPeriod = tokenBucket.ReplenishmentPeriod,
                    AutoReplenishment = true,
                    QueueLimit = InnerQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            SharpLinkFixedWindowLimitOptions fixedWindow => new FixedWindowRateLimiter(
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = fixedWindow.PermitLimit,
                    Window = fixedWindow.Window,
                    AutoReplenishment = true,
                    QueueLimit = InnerQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            SharpLinkSlidingWindowLimitOptions slidingWindow => new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = slidingWindow.PermitLimit,
                    Window = slidingWindow.Window,
                    SegmentsPerWindow = slidingWindow.SegmentsPerWindow,
                    AutoReplenishment = true,
                    QueueLimit = InnerQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            _ => throw new InvalidOperationException("Admission rate state requires one rate policy.")
        };
        return new AdmissionRateState(limiter, definition);
    }

    public override TimeSpan? IdleDuration => _limiter.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _limiter.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
        => _limiter.AttemptAcquire(permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
        => _limiter.AcquireAsync(permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _limiter.Dispose();
    }
}
