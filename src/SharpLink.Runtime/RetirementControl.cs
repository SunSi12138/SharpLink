namespace SharpLink.Runtime;

internal enum RetirementOrigin : byte
{
    PublicUnregister,
    PublicReplacement,
    Stop
}

// Per-generation cold coordination, not a generic task directory. The host's
// existing registry gate owns election/publication; its original physical task
// owns actions and consumes Driver.Task. The outer observer owns final refund.
internal sealed class RetirementControl
{
    // Logical capacity inventory is documented with the SOURCE checkpoint, not
    // inferred from CLR object sizes. All slots are admitted at module creation.
    internal const int ReservedSlots = 39;

    private readonly TaskCompletionSource<bool> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _reservationLease;
    private Func<bool, Exception?>? _actions;
    private readonly ColdMetadataDomain _domain;
    private readonly RetirementGraceBudget? _budget;
    private RetirementCancellationDriver? _driver;
    private readonly ColdMetadataReservation _capacity;
    private bool _releaseAttempted;

    internal bool PhysicalExited { get; private set; }
    internal bool ReservationLeaseReturned { get; private set; }
    internal bool CapacityRefunded { get; private set; }
    internal Exception? ReservationReleaseFailure { get; private set; }
    internal void RecordPhysicalExit(bool noRetainedTimer) => PhysicalExited = noRetainedTimer;

    internal RetirementControl(SharpLinkDynamicModule module, ColdMetadataDomain domain,
        RetirementOrigin origin, RetirementGraceBudget? budget, Func<bool, Exception?>? actions,
        bool deferDriver = false)
    {
        Module = module;
        Origin = origin;
        _actions = actions;
        _reservationLease = module.RetainRetirementCapacity();
        _domain = domain;
        _budget = budget;
        _capacity = module.RetirementCapacity;
        if (!deferDriver)
        {
            try { InitializeDriver(); }
            catch
            {
                Interlocked.Exchange(ref _reservationLease, null)?.Dispose();
                throw;
            }
        }
    }

    // Client coordinated selection roots this control before the first original
    // suspended task is created. Server/direct construction keeps its existing order.
    internal void InitializeDriver()
    {
        if (_driver is not null) throw new InvalidOperationException("Retirement driver already exists.");
        if (ExecutionContext.IsFlowSuppressed())
            _driver = new RetirementCancellationDriver(Module, _domain, _budget, _start.Task,
                forceRequested: Origin == RetirementOrigin.Stop);
        else
        {
            using (ExecutionContext.SuppressFlow())
                _driver = new RetirementCancellationDriver(Module, _domain, _budget, _start.Task,
                    forceRequested: Origin == RetirementOrigin.Stop);
        }
    }

    internal RetirementOrigin Origin { get; }
    internal SharpLinkDynamicModule Module { get; }
    internal RetirementCancellationDriver Driver => _driver ??
        throw new InvalidOperationException("Retirement driver has not been initialized.");
    internal RetirementCancellationDriver? CreatedDriver => _driver;
    internal Task<bool> StartDecision => _start.Task;
    internal TaskCompletionSource<Exception?> Tracking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Exception?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<SharpLinkAssemblyUnregisterResult?> GraceDecision { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Only mutated under the host registry gate, BEFORE the gate-exit activation.
    internal bool IsCommitted { get; private set; }
    internal void MarkCommitted() => IsCommitted = true;

    // Must run outside the host gate and before any postcommit callback/cleanup.
    internal void Activate() => _start.TrySetResult(IsCommitted);

    // The actual physical worker calls this once, on Commit or Abort. Captured
    // source owners transfer to that worker; public using/finally must not dispose
    // the same ownership units after a committed transfer.
    internal Exception? RunActions(bool committed)
        => Interlocked.Exchange(ref _actions, null)?.Invoke(committed);

    internal void RequestForce() => Driver.RequestForce();

    // Observer performs this AFTER physical exit and BEFORE deciding dictionary
    // removal. A refund failure remains an observable, discoverable occurrence.
    internal void ReleaseReservation()
    {
        if (_releaseAttempted) return;
        _releaseAttempted = true;
        try
        {
            // An aborted stage returns only its borrowed lease: the still-running
            // module must keep its prepaid capacity for a subsequent election.
            // A released module no longer needs the original storage reference.
            if (Module.State == SharpLinkDynamicModuleState.Released)
            {
                if (!_capacity.OriginalReleaseSucceeded)
                {
                    if (_capacity.IsDetached)
                        throw new InvalidOperationException("The original capacity release has an uncertified outcome.");
                    _capacity.Dispose();
                }
                if (!_capacity.OriginalReleaseSucceeded || !_capacity.IsDetached)
                    throw new InvalidOperationException("Released retirement capacity remains attached.");
            }
            _reservationLease!.Dispose();
            ReservationLeaseReturned = true;
            CapacityRefunded = Module.State == SharpLinkDynamicModuleState.Released && _capacity.IsRefunded;
            if (Module.State == SharpLinkDynamicModuleState.Released && !CapacityRefunded)
                throw new InvalidOperationException("The exact released module capacity has not refunded.");
            _reservationLease = null;
        }
        catch (Exception failure)
        {
            ReservationReleaseFailure = failure;
            throw;
        }
    }
}

// A precommit using guard. Once the rooted physical owner takes its exact
// resource, public unwinding becomes a no-op, not a second Dispose invocation.
internal sealed class RetirementTransfer<T>(T resource) : IDisposable where T : class, IDisposable
{
    private T? _resource = resource;
    internal T Resource => _resource ?? throw new ObjectDisposedException(nameof(RetirementTransfer<T>));
    internal void Relinquish() => _resource = null;
    public void Dispose() => Interlocked.Exchange(ref _resource, null)?.Dispose();

    // A replacement candidate is not the elected old-module owner. Charge its
    // using guards to THIS admitted preparation, including concurrent candidates,
    // rather than pretending one per-module reservation covers them all.
    internal static RetirementTransfer<T> CreateCharged(T resource, ColdMetadataPreparation preparation, int slots)
    {
        try
        {
            preparation.ReserveCollection(slots);
            return new RetirementTransfer<T>(resource);
        }
        catch (Exception original)
        {
            try { resource.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
}

internal static class RetirementWaiter
{
    // Arbitration consumes no original outcome. Outside this method the caller
    // awaits the ORIGINAL task exactly once, so a user OCE is never classified by
    // exception type, token or reference. A completed original wins the boundary;
    // otherwise cancellation belongs to this private wait, not its physical owner.
    internal static async ValueTask WaitForCompletionAsync(Task original, CancellationToken cancellationToken)
    {
        if (original.IsCompleted) return;
        var wait = cancellationToken.CanBeCanceled ? original.WaitAsync(cancellationToken) : original;
        await wait.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (original.IsCompleted) return;
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("A retirement waiter ended without source completion or caller cancellation.");
    }
}
