using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Minimal limiter surface used by the Admission composite. Async waiting is only entered after the
/// server-scoped kernel has reserved exactly one outer queue slot for the Request.
/// </summary>
internal interface IAdmissionLimiter
{
    RateLimitLease AttemptAcquire(int permitCount);

    ValueTask<RateLimitLease> AcquireAsync(int permitCount, CancellationToken cancellationToken);
}

/// <summary>
/// Stable concurrency state whose target may be changed without replacing active holders or queued
/// waiters. The state is shared by every overlapping program generation that binds the same logical
/// concurrency component.
/// </summary>
internal sealed class ResizableConcurrencyState : IAdmissionLimiter, IDisposable
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _permitLimit;
    private int _active;
    private int _disposed;

    internal ResizableConcurrencyState(int permitLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        _permitLimit = permitLimit;
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
                return _waiters.Count;
        }
    }

    public RateLimitLease AttemptAcquire(int permitCount)
    {
        ValidatePermitCount(permitCount);
        lock (_gate)
        {
            if (_disposed != 0)
                return FailedLease.Instance;

            // Do not let a new immediate caller barge ahead of an already queued Request.
            if (_waiters.Count != 0 || _active >= _permitLimit)
                return FailedLease.Instance;

            _active++;
            return new ConcurrencyLease(this);
        }
    }

    public ValueTask<RateLimitLease> AcquireAsync(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        Waiter? waiter = null;
        lock (_gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

            if (_waiters.Count == 0 && _active < _permitLimit)
            {
                _active++;
                return ValueTask.FromResult<RateLimitLease>(new ConcurrencyLease(this));
            }

            waiter = new Waiter(this, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
        }

        var registration = cancellationToken.Register(
            static state => ((Waiter)state!).Owner.CancelWaiter((Waiter)state!),
            waiter);
        waiter.SetRegistration(registration);
        return new ValueTask<RateLimitLease>(waiter.Task);
    }

    /// <summary>
    /// Commits a prevalidated target. Existing holders remain valid. Increasing capacity grants the
    /// oldest eligible queued Requests immediately; shrinking waits for natural releases.
    /// </summary>
    internal void Resize(int permitLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        List<Waiter>? granted;
        lock (_gate)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ResizableConcurrencyState));
            _permitLimit = permitLimit;
            granted = GrantWaitersLocked();
        }
        CompleteGranted(granted);
    }

    public void Dispose()
    {
        List<Waiter>? failed = null;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                node.Value.Node = null;
                (failed ??= []).Add(node.Value);
            }
        }

        if (failed is not null)
            foreach (var waiter in failed)
                waiter.CompleteFailed();
    }

    private void ReleasePermit()
    {
        List<Waiter>? granted = null;
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

    private List<Waiter>? GrantWaitersLocked()
    {
        List<Waiter>? granted = null;
        while (_active < _permitLimit && _waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            var waiter = node.Value;
            waiter.Node = null;
            _active++;
            (granted ??= []).Add(waiter);
        }
        return granted;
    }

    private void CancelWaiter(Waiter waiter)
    {
        var removed = false;
        lock (_gate)
        {
            if (waiter.Node is { } node)
            {
                _waiters.Remove(node);
                waiter.Node = null;
                removed = true;
            }
        }
        if (removed)
            waiter.CompleteCanceled();
    }

    private void CompleteGranted(List<Waiter>? granted)
    {
        if (granted is null)
            return;
        foreach (var waiter in granted)
            waiter.CompleteGranted(this);
    }

    private static void ValidatePermitCount(int permitCount)
    {
        if (permitCount != 1)
            throw new ArgumentOutOfRangeException(nameof(permitCount), "Admission limiters acquire exactly one permit.");
    }

    private sealed class Waiter(
        ResizableConcurrencyState owner,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<RateLimitLease> _source = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal ResizableConcurrencyState Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal LinkedListNode<Waiter>? Node { get; set; }
        internal Task<RateLimitLease> Task => _source.Task;

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
            _source.TrySetResult(new ConcurrencyLease(state));
        }

        internal void CompleteCanceled()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            _source.TrySetCanceled(CancellationToken);
        }

        internal void CompleteFailed()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            _source.TrySetResult(FailedLease.Instance);
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
internal sealed class AdmissionRateState : IAdmissionLimiter, IDisposable
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

    public RateLimitLease AttemptAcquire(int permitCount) => _limiter.AttemptAcquire(permitCount);

    public ValueTask<RateLimitLease> AcquireAsync(int permitCount, CancellationToken cancellationToken)
        => _limiter.AcquireAsync(permitCount, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
