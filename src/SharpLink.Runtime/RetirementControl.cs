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
    internal const int ReservedSlots = 31;

    private readonly TaskCompletionSource<bool> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _reservationLease;
    private Func<bool, Exception?>? _actions;

    internal RetirementControl(SharpLinkDynamicModule module, ColdMetadataDomain domain,
        RetirementOrigin origin, RetirementGraceBudget? budget, Func<bool, Exception?>? actions)
    {
        Module = module;
        Origin = origin;
        _actions = actions;
        _reservationLease = module.RetainRetirementCapacity();
        try
        {
            // Do not retain a caller's active preparation AsyncLocal in the new
            // suspended cancellation task. Suppression is synchronous, never
            // held over an await, and does not alter user token registrations.
            if (ExecutionContext.IsFlowSuppressed())
                Driver = new RetirementCancellationDriver(module, domain, budget, _start.Task,
                    forceRequested: origin == RetirementOrigin.Stop);
            else
            {
                using (ExecutionContext.SuppressFlow())
                    Driver = new RetirementCancellationDriver(module, domain, budget, _start.Task,
                        forceRequested: origin == RetirementOrigin.Stop);
            }
        }
        catch
        {
            Interlocked.Exchange(ref _reservationLease, null)?.Dispose();
            throw;
        }
    }

    internal RetirementOrigin Origin { get; }
    internal SharpLinkDynamicModule Module { get; }
    internal RetirementCancellationDriver Driver { get; }
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
        => Interlocked.Exchange(ref _reservationLease, null)?.Dispose();
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
