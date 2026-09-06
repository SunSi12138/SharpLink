using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Stable concurrency state whose target may be changed without replacing active holders or queued
/// waiters. The state is shared by every overlapping program generation that binds the same logical
/// concurrency component.
/// </summary>
internal sealed class ResizableConcurrencyState : RateLimiter
{
    private const long UnversionedTarget = long.MinValue;

    private readonly Lock _gate = new();
    private readonly AdmissionStateKernel? _targetVersionOwner;
    private Waiter? _waiterHead;
    private Waiter? _waiterTail;
    private int _waitingCount;
    private int _permitLimit;
    private int _active;
    private int _disposed;
    private int _fastRejectUnavailable;

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

    internal bool TracksTargetVersion => _targetVersionOwner is not null;

    /// <summary>Deterministic test seam before an immediate acquisition inspects this state.</summary>
    internal Action? BeforeAttemptAcquireForTests { get; set; }

    /// <summary>
    /// Deterministic test seam after a stable grant version is read but before the state lock is
    /// acquired. Production grant correctness must survive an update starting in this interval.
    /// </summary>
    internal Action? AfterStableGrantVersionReadForTests { get; set; }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    internal bool IsLeaseFromTargetVersion(RateLimitLease lease, long targetVersion)
        => lease is VersionedConcurrencyLease concurrencyLease &&
           ReferenceEquals(concurrencyLease.State, this) &&
           concurrencyLease.TargetVersion == targetVersion;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);

        if (Volatile.Read(ref _fastRejectUnavailable) != 0)
            return FailedLease.Instance;

        return AttemptAcquireStableCore();
    }

    private RateLimitLease AttemptAcquireStableCore()
    {
        BeforeAttemptAcquireForTests?.Invoke();

        lock (_gate)
        {
            if (_disposed != 0)
                return FailedLease.Instance;

            if (_waitingCount != 0 || _active >= _permitLimit)
                return FailedLease.Instance;

            _active++;
            RefreshFastRejectStateLocked();
            return new ConcurrencyLease(this);
        }
    }

    internal ValueTask<RateLimitLease> AcquireAsyncForAdmission(
        bool captureTargetVersion,
        CancellationToken cancellationToken)
    {
        if (!captureTargetVersion || _targetVersionOwner is null)
            return AcquireAsyncUnversioned(cancellationToken);
        return AcquireAsyncVersioned(cancellationToken);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        return _targetVersionOwner is null
            ? AcquireAsyncUnversioned(cancellationToken)
            : AcquireAsyncVersioned(cancellationToken);
    }

    private ValueTask<RateLimitLease> AcquireAsyncVersioned(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

        var versionOwner = _targetVersionOwner ??
            throw new InvalidOperationException("Versioned admission acquisition requires a target owner.");
        while (true)
        {
            var targetVersion = versionOwner.ReadStableConcurrencyTargetVersion();
            Waiter? waiter = null;
            RateLimitLease? immediateLease = null;

            lock (_gate)
            {
                if (_disposed != 0)
                    return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);
                if (!versionOwner.IsConcurrencyTargetVersionCurrent(targetVersion))
                    continue;

                if (_waitingCount == 0 && _active < _permitLimit)
                {
                    _active++;
                    if (!versionOwner.IsConcurrencyTargetVersionCurrent(targetVersion))
                    {
                        _active--;
                        continue;
                    }
                    RefreshFastRejectStateLocked();
                    immediateLease = new VersionedConcurrencyLease(this, targetVersion);
                }
                else
                {
                    waiter = new Waiter(this, cancellationToken, captureTargetVersion: true);
                    EnqueueWaiterLocked(waiter);
                }
            }

            if (immediateLease is not null)
                return ValueTask.FromResult<RateLimitLease>(immediateLease);
            if (waiter is null)
                continue;

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.UnsafeRegister(
                    static state => ((Waiter)state!).Owner.CancelWaiter((Waiter)state!),
                    waiter);
                waiter.SetRegistration(registration);
            }
            return new ValueTask<RateLimitLease>(waiter.Task);
        }
    }

    private ValueTask<RateLimitLease> AcquireAsyncUnversioned(CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (_disposed != 0)
                return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);

            if (_waitingCount == 0 && _active < _permitLimit)
            {
                _active++;
                RefreshFastRejectStateLocked();
                return ValueTask.FromResult<RateLimitLease>(new ConcurrencyLease(this));
            }

            waiter = new Waiter(this, cancellationToken, captureTargetVersion: false);
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

    internal void Resize(int permitLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        Waiter? granted = null;
        lock (_gate)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ResizableConcurrencyState));
            _permitLimit = permitLimit;
            if (_targetVersionOwner is null)
                granted = GrantWaitersLocked();
            RefreshFastRejectStateLocked();
        }
        CompleteGranted(granted, UnversionedTarget);
    }

    internal void GrantWaitersAfterTargetCommit()
        => GrantWaitersForStableTarget();

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
            RefreshFastRejectStateLocked();
            failed = DetachAllWaitersLocked();
        }

        CompleteFailed(failed);
    }

    private void ReleasePermit()
    {
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException("Admission concurrency permit count underflowed.");
            _active--;
            RefreshFastRejectStateLocked();
            if (_disposed != 0)
                return;
        }

        GrantWaitersForStableTarget();
    }

    private void GrantWaitersForStableTarget()
    {
        var versionOwner = _targetVersionOwner;
        if (versionOwner is null)
        {
            Waiter? granted;
            lock (_gate)
            {
                if (_disposed != 0)
                    return;
                granted = GrantWaitersLocked();
                RefreshFastRejectStateLocked();
            }
            CompleteGranted(granted, UnversionedTarget);
            return;
        }

        while (true)
        {
            var targetVersion = versionOwner.ReadStableConcurrencyTargetVersion();
            AfterStableGrantVersionReadForTests?.Invoke();

            Waiter? granted = null;
            var retry = false;
            lock (_gate)
            {
                if (_disposed != 0)
                    return;

                if (!versionOwner.IsConcurrencyTargetVersionCurrent(targetVersion))
                {
                    retry = true;
                }
                else
                {
                    granted = GrantWaitersLocked();
                    RefreshFastRejectStateLocked();
                }
            }

            if (retry)
                continue;

            CompleteGranted(granted, targetVersion);
            return;
        }
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

    private void RefreshFastRejectStateLocked()
    {
        var unavailable = _disposed != 0 || _active >= _permitLimit ? 1 : 0;
        if (_fastRejectUnavailable != unavailable)
            Volatile.Write(ref _fastRejectUnavailable, unavailable);
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

    private void CompleteGranted(Waiter? granted, long targetVersion)
    {
        while (granted is not null)
        {
            var next = granted.Next;
            granted.Next = null;
            granted.CompleteGranted(this, targetVersion);
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
        CancellationToken cancellationToken,
        bool captureTargetVersion)
        : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private CancellationTokenRegistration _registration;
        private int _completed;

        internal ResizableConcurrencyState Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal Waiter? Previous { get; set; }
        internal Waiter? Next { get; set; }
        internal bool IsQueued { get; set; }
        internal bool CaptureTargetVersion { get; } = captureTargetVersion;

        internal void SetRegistration(CancellationTokenRegistration registration)
        {
            _registration = registration;
            if (Volatile.Read(ref _completed) != 0)
                registration.Dispose();
        }

        internal void CompleteGranted(ResizableConcurrencyState state, long targetVersion)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _registration.Dispose();
            TrySetResult(CaptureTargetVersion
                ? new VersionedConcurrencyLease(state, targetVersion)
                : new ConcurrencyLease(state));
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

    private sealed class ConcurrencyLease(ResizableConcurrencyState state) : RateLimitLease
    {
        private ResizableConcurrencyState? _owner = state;

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

    private sealed class VersionedConcurrencyLease(
        ResizableConcurrencyState state,
        long targetVersion) : RateLimitLease
    {
        private ResizableConcurrencyState? _owner = state;

        internal ResizableConcurrencyState? State => Volatile.Read(ref _owner);
        internal long TargetVersion { get; } = targetVersion;

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

/// <summary>
/// Immutable rate-policy view. Every non-partition FixedWindow uses a stable shared counter;
/// TokenBucket, SlidingWindow and partition rate state keep the existing #333 implementation.
/// Algorithm identity changes are generation boundaries rather than history translations.
/// </summary>
internal sealed class AdmissionRateState : RateLimiter
{
    private readonly AdmissionDynamicRateState? _state;
    private readonly DynamicFixedWindowRateLimiter? _fixedWindow;
    private readonly AdmissionRateTransitionLineage _lineage;
    private readonly AdmissionRateStateDefinition _definition;

    private AdmissionRateState(AdmissionDynamicRateState state)
    {
        _state = state;
        _lineage = state.Lineage;
        _definition = state.Definition;
    }

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowRateLimiter fixedWindow,
        AdmissionRateTransitionLineage lineage)
    {
        _definition = definition;
        _fixedWindow = fixedWindow;
        _lineage = lineage;
    }

    internal AdmissionRateStateDefinition Definition => _definition;

    internal AdmissionRateTransitionLineage Lineage => _lineage;

    internal int WaitingCount => _fixedWindow?.WaitingCount ?? _state!.WaitingCount;

    internal long TransitionDebtForDiagnostics => _state?.TransitionDebtForDiagnostics ?? 0;

    internal long TransitionBarrierExpiryForDiagnostics => _state?.TransitionBarrierExpiryForDiagnostics ?? 0;

    internal DynamicFixedWindowRateLimiter? FixedWindowForTests => _fixedWindow;

    internal void OnPublished() => _fixedWindow?.OnPublished();

    internal static AdmissionRateState Create(
        SharpLinkAdmissionRuleOptions options,
        TimeProvider timeProvider,
        AdmissionRateState? transitionSource = null)
    {
        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        var canUseStableFixedWindow =
            definition.Kind == AdmissionRateStateKind.FixedWindow &&
            options is not SharpLinkPartitionAdmissionOptions;
        if (canUseStableFixedWindow)
        {
            var fixedOptions = (SharpLinkFixedWindowLimitOptions)options.RateLimit!;
            var window = TimeSpan.FromTicks(definition.PeriodTicks);
            if (transitionSource?._fixedWindow is { } sourceFixed)
            {
                if (fixedOptions.UpdateActivation == DynamicFixedWindowActivationMode.Immediate &&
                    definition.PeriodTicks != transitionSource.Definition.PeriodTicks)
                {
                    throw new InvalidOperationException(
                        "Immediate FixedWindow updates may change PermitLimit only. Change Window with NextWindowBoundary activation.");
                }

                return new AdmissionRateState(
                    definition,
                    sourceFixed.CreateSuccessor(
                        definition.Limit,
                        window,
                        fixedOptions.UpdateActivation),
                    transitionSource._lineage);
            }

            return new AdmissionRateState(
                definition,
                new DynamicFixedWindowRateLimiter(definition.Limit, window, timeProvider),
                new AdmissionRateTransitionLineage());
        }

        // TokenBucket/SlidingWindow and partitions keep the existing #333 implementation. A source
        // from the specialized FixedWindow model deliberately starts a fresh algorithm generation.
        var state = new AdmissionDynamicRateState(
            definition,
            timeProvider,
            transitionSource?._state?.Lineage);
        return new AdmissionRateState(state);
    }

    internal void CommitTransitionTo(AdmissionRateState? target)
    {
        if (_fixedWindow is not null)
        {
            if (target?._fixedWindow is not null && ReferenceEquals(_lineage, target._lineage))
                _fixedWindow.CommitTransitionTo(target._fixedWindow);
            return;
        }

        if (target?._state is not null && ReferenceEquals(_lineage, target._lineage))
            _state!.CommitTransitionTo(target._state);
        else
            _state!.CommitTransitionTo(null);
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
        => _fixedWindow?.AttemptAcquire(permitCount) ?? _state!.AttemptAcquire(permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
        => _fixedWindow?.AcquireAsync(permitCount, cancellationToken) ??
           _state!.AcquireAsync(permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _fixedWindow?.Dispose();
        _state?.Dispose();
    }
}
