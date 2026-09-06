using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal enum DynamicFixedWindowActivationMode
{
    Immediate,
    NextWindowBoundary
}

/// <summary>Immutable FixedWindow policy view carried directly by one AdmissionProgram rate state.</summary>
internal sealed partial class AdmissionRateState
{
    private const int FixedPrepared = 0;
    private const int FixedCommitted = 1;
    private const int FixedPublished = 2;
    private const int FixedDisposed = -1;

    private readonly Counter? _fixedCounter;
    private readonly long _fixedSequence;
    private readonly DynamicFixedWindowActivationMode _fixedActivationMode;
    private long _fixedActivationBoundary;
    private int _fixedLifecycleState;

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        TimeProvider timeProvider)
    {
        _definition = definition;
        _fixedCounter = new Counter(
            definition.Limit,
            TimeSpan.FromTicks(definition.PeriodTicks),
            timeProvider);
        _fixedSequence = 1;
        _fixedActivationMode = DynamicFixedWindowActivationMode.Immediate;
        _fixedLifecycleState = FixedPublished;
    }

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        Counter counter,
        long sequence,
        DynamicFixedWindowActivationMode activationMode)
    {
        _definition = definition;
        _fixedCounter = counter;
        _fixedSequence = sequence;
        _fixedActivationMode = activationMode;
    }

    internal AdmissionRateState? FixedWindowForTests => _fixedCounter is null ? null : this;
    internal int PermitLimit => _definition.Limit;
    internal TimeSpan Window => TimeSpan.FromTicks(_definition.PeriodTicks);
    internal DynamicFixedWindowActivationMode ActivationModeForTests => _fixedActivationMode;
    internal long ConsumedForTests => _fixedCounter!.Consumed;
    internal int ActiveLimitForTests => _fixedCounter!.ActiveLimit;
    internal int QueuedLimitForTests => _fixedCounter!.QueuedLimit;
    internal TimeSpan ActiveWindowForTests => _fixedCounter!.ActiveWindow;
    internal bool HasPendingWindowForTests => _fixedCounter!.HasPendingTarget;
    internal object CounterIdentityForTests => _fixedCounter!;

    private AdmissionRateState CreateFixedSuccessor(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowActivationMode? activationMode)
    {
        ThrowIfFixedDisposed();
        var counter = _fixedCounter ??
            throw new InvalidOperationException("FixedWindow successor requires a stable counter.");
        var requestedWindow = counter.ToTimestampTicks(definition.PeriodTicks);
        var resolvedActivation = counter.ResolveActivation(requestedWindow, activationMode);
        return counter.CreateSuccessor(definition, resolvedActivation);
    }

    private void CommitFixedTransitionTo(AdmissionRateState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfFixedDisposed();
        if (target._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate &&
            target._definition.PeriodTicks != _definition.PeriodTicks)
        {
            throw new InvalidOperationException(
                "Immediate FixedWindow updates may change PermitLimit only. Change Window with NextWindowBoundary activation.");
        }
        _fixedCounter!.CommitTransition(this, target);
    }

    private void OnFixedPublished()
    {
        ThrowIfFixedDisposed();
        _fixedCounter!.Publish(this);
    }

    private RateLimitLease AttemptAcquireFixed(int permitCount)
    {
        ValidateFixedPermitCount(permitCount);
        if (Volatile.Read(ref _fixedLifecycleState) == FixedDisposed)
            return AdmissionRateLeases.Failed;
        _fixedCounter!.Publish(this);
        return _fixedCounter.AttemptAcquire(this);
    }

    private ValueTask<RateLimitLease> AcquireFixedAsync(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidateFixedPermitCount(permitCount);
        if (Volatile.Read(ref _fixedLifecycleState) == FixedDisposed)
            return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Failed);
        _fixedCounter!.Publish(this);
        return _fixedCounter.AcquireAsync(cancellationToken);
    }

    private void DisposeFixed()
    {
        if (Interlocked.Exchange(ref _fixedLifecycleState, FixedDisposed) == FixedDisposed)
            return;
        _fixedCounter!.ReleaseView();
    }

    private void FinalizeFixedForCommit(long activationBoundary)
    {
        if (Interlocked.CompareExchange(ref _fixedLifecycleState, FixedCommitted, FixedPrepared) != FixedPrepared)
            throw new InvalidOperationException("FixedWindow policy view was committed more than once.");
        _fixedActivationBoundary = activationBoundary;
    }

    private void MarkFixedPublishedLocked()
    {
        if (Interlocked.CompareExchange(ref _fixedLifecycleState, FixedPublished, FixedCommitted) != FixedCommitted)
            throw new InvalidOperationException("FixedWindow policy view was published from an invalid state.");
    }

    private void ThrowIfFixedDisposed()
    {
        if (Volatile.Read(ref _fixedLifecycleState) == FixedDisposed)
            throw new ObjectDisposedException(nameof(AdmissionRateState));
    }

    private static void ValidateFixedPermitCount(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                "Admission FixedWindow limiters acquire exactly one permit.");
        }
    }
}
