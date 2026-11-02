namespace SharpLink.Runtime;

internal enum GeneratedPhysicalFailureStage : byte
{
    PendingOperation,
    EnumeratorDispose,
    LifetimeCancellationDispose
}

// The role is selected before starting the original operation, not from its eventual outcome.
internal enum GeneratedPendingOperationRole : byte
{
    IndependentPhysical,
    InvocationMoveNext
}

// A result-ownership receipt, not another physical lease. The internal Server issuer validates
// its captured call-state generation; Runtime checks the exact owner, operation and request.
internal readonly record struct GeneratedInvocationOutcomeReceipt(
    IGeneratedStreamPhysicalOwner Owner, Task Operation, long RequestId, long Generation);

// Existing invocation/service/scope owner; never an additional module acquisition or item counter.
internal interface IGeneratedStreamPhysicalOwner
{
    void TryAcceptInvocationOutcome(Task operation, long expectedRequestId,
        ref GeneratedInvocationOutcomeReceipt? receipt) { }
    void RetainPhysicalTail();
    void ReleasePhysicalTail();
    void ReportPhysicalFailure(GeneratedPhysicalFailureStage stage, Exception failure, ref bool accepted);
    void TrackPhysicalTail(Task task, ref FrameworkTaskRegistration? registration);
}

internal sealed partial class RpcSession
{
    private static async ValueTask FinishGeneratedEnumeratorAsync<T>(
        IAsyncEnumerator<T> enumerator, Task? pendingOperation,
        CancellationTokenSource lifetimeCancellation, IGeneratedStreamPhysicalOwner? physicalOwner,
        bool terminalSelected, Exception? lifetimeFailure,
        GeneratedPendingOperationRole pendingRole = GeneratedPendingOperationRole.IndependentPhysical,
        long expectedRequestId = 0)
    {
        Exception? residual = null;
        // Capture synchronously while the invocation still owns its permit. Neither the
        // detached task nor a completed-source branch may look up a later pooled generation.
        var invocationAccepted = TryAcceptGeneratedInvocationOutcome(physicalOwner, pendingOperation,
            pendingRole, expectedRequestId, ref residual);
        if (pendingOperation is { IsCompleted: false })
        {
            // Never dispose concurrently with the actual MoveNext/codec/send operation.
            var retained = false;
            Exception? retainFailure = null;
            if (physicalOwner is not null)
            {
                try { physicalOwner.RetainPhysicalTail(); retained = true; }
                catch (Exception error) { retainFailure = error; }
            }
            var tail = CompleteGeneratedOperationTailAsync(pendingOperation, enumerator,
                lifetimeCancellation, physicalOwner, retained, lifetimeFailure,
                PhysicalTaskFailureException.CombineRaw(residual, retainFailure), invocationAccepted);
            if (retained)
                await TrackOrJoinGeneratedPhysicalTailAsync(tail, physicalOwner!).ConfigureAwait(false);
            else
                await tail.ConfigureAwait(false); // No owner or failed retain: actual inline join, never orphan.
            return;
        }

        if (pendingOperation is not null)
        {
            try { await pendingOperation.ConfigureAwait(false); }
            catch (Exception error)
            {
                // Readiness changes the join mechanics, never the operation's stage owner.
                if (!invocationAccepted)
                    ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.PendingOperation,
                        error, ref residual);
            }
        }
        ValueTask dispose = default;
        var disposeStarted = false;
        try { dispose = enumerator.DisposeAsync(); disposeStarted = true; }
        catch (Exception error)
        {
            ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.EnumeratorDispose,
                error, ref residual);
        }
        if (disposeStarted && !dispose.IsCompleted && terminalSelected)
        {
            var retained = false;
            if (physicalOwner is not null)
            {
                try { physicalOwner.RetainPhysicalTail(); retained = true; }
                catch (Exception error) { residual = PhysicalTaskFailureException.CombineRaw(residual, error); }
            }
            // The original ValueTask is consumed once; this is the existing actual dispose tail.
            var tail = CompleteGeneratedDisposeTailAsync(dispose.AsTask(), lifetimeCancellation,
                physicalOwner, retained, lifetimeFailure, residual);
            if (retained)
                await TrackOrJoinGeneratedPhysicalTailAsync(tail, physicalOwner!).ConfigureAwait(false);
            else
                await tail.ConfigureAwait(false);
            return;
        }
        if (disposeStarted)
        {
            try { await dispose.ConfigureAwait(false); }
            catch (Exception error)
            {
                ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.EnumeratorDispose,
                    error, ref residual);
            }
        }
        DisposeGeneratedLifetime(lifetimeCancellation, physicalOwner, lifetimeFailure, ref residual);
        if (residual is not null) throw PhysicalTaskFailureException.From(residual);
    }

    private static async Task CompleteGeneratedOperationTailAsync<T>(
        Task operation, IAsyncEnumerator<T> enumerator, CancellationTokenSource lifetimeCancellation,
        IGeneratedStreamPhysicalOwner? physicalOwner, bool retained, Exception? lifetimeFailure,
        Exception? residual, bool invocationAccepted)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (!invocationAccepted)
                ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.PendingOperation,
                    error, ref residual);
        }
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.EnumeratorDispose,
                error, ref residual);
        }
        DisposeGeneratedLifetime(lifetimeCancellation, physicalOwner, lifetimeFailure, ref residual);
        if (retained)
        {
            try { physicalOwner!.ReleasePhysicalTail(); }
            catch (Exception error) { residual = PhysicalTaskFailureException.CombineRaw(residual, error); }
        }
        if (residual is not null) throw PhysicalTaskFailureException.From(residual);
    }

    private static async Task CompleteGeneratedDisposeTailAsync(
        Task dispose, CancellationTokenSource lifetimeCancellation, IGeneratedStreamPhysicalOwner? physicalOwner,
        bool retained, Exception? lifetimeFailure, Exception? residual)
    {
        try { await dispose.ConfigureAwait(false); }
        catch (Exception error)
        {
            ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.EnumeratorDispose,
                error, ref residual);
        }
        DisposeGeneratedLifetime(lifetimeCancellation, physicalOwner, lifetimeFailure, ref residual);
        if (retained)
        {
            try { physicalOwner!.ReleasePhysicalTail(); }
            catch (Exception error) { residual = PhysicalTaskFailureException.CombineRaw(residual, error); }
        }
        if (residual is not null) throw PhysicalTaskFailureException.From(residual);
    }

    private static bool TryAcceptGeneratedInvocationOutcome(IGeneratedStreamPhysicalOwner? owner,
        Task? operation, GeneratedPendingOperationRole role, long expectedRequestId, ref Exception? residual)
    {
        if (owner is null || operation is null || role != GeneratedPendingOperationRole.InvocationMoveNext)
            return false;

        GeneratedInvocationOutcomeReceipt? receipt = null;
        try { owner.TryAcceptInvocationOutcome(operation, expectedRequestId, ref receipt); }
        catch (Exception error) { residual = PhysicalTaskFailureException.CombineRaw(residual, error); }

        // A hook can publish through ref and then throw. Always validate the surviving value:
        // valid acceptance is irrevocable, but a malformed receipt never consumes the source.
        if (receipt is not { } accepted)
            return false;
        if (!ReferenceEquals(accepted.Owner, owner) || !ReferenceEquals(accepted.Operation, operation) ||
            accepted.RequestId != expectedRequestId)
        {
            residual = PhysicalTaskFailureException.CombineRaw(residual,
                new InvalidOperationException("The generated invocation outcome receipt does not match its exact owner, operation and request."));
            return false;
        }
        return true;
    }

    private static void ReportGeneratedPhysicalFailure(IGeneratedStreamPhysicalOwner? owner,
        GeneratedPhysicalFailureStage stage, Exception failure, ref Exception? residual)
    {
        if (owner is null)
        {
            residual = PhysicalTaskFailureException.CombineRaw(residual, failure);
            return;
        }
        var accepted = false;
        Exception? reportingFailure = null;
        try { owner.ReportPhysicalFailure(stage, failure, ref accepted); }
        catch (Exception error) { reportingFailure = error; }
        // Acceptance is irrevocable even if a later notification throws. Never copy a
        // permit-owned raw failure into the tail's separately supervised infrastructure outcome.
        if (!accepted)
        {
            residual = PhysicalTaskFailureException.CombineRaw(residual, failure);
            reportingFailure ??= new InvalidOperationException("The physical owner rejected a generated tail failure.");
        }
        residual = PhysicalTaskFailureException.CombineRaw(residual, reportingFailure);
    }

    private static void DisposeGeneratedLifetime(CancellationTokenSource lifetime,
        IGeneratedStreamPhysicalOwner? owner, Exception? lifetimeFailure, ref Exception? residual)
    {
        try { lifetime.Dispose(); }
        catch (Exception error) { lifetimeFailure = PhysicalTaskFailureException.CombineRaw(lifetimeFailure, error); }
        if (lifetimeFailure is not null)
            ReportGeneratedPhysicalFailure(owner, GeneratedPhysicalFailureStage.LifetimeCancellationDispose,
                lifetimeFailure, ref residual);
    }

    private static async ValueTask TrackOrJoinGeneratedPhysicalTailAsync(Task tail, IGeneratedStreamPhysicalOwner owner)
    {
        FrameworkTaskRegistration? receipt = null;
        Exception? failure = null;
        try
        {
            owner.TrackPhysicalTail(tail, ref receipt);
            if (receipt is null || !ReferenceEquals(receipt.Task, tail) || !receipt.IsStrictPhysical)
                throw new InvalidOperationException("The generated physical tail did not receive its exact strict registration receipt.");
            return; // Existing supervisor now owns the actual detached task and its residual.
        }
        catch (Exception error) { failure = error; }
        var accepted = receipt is not null && ReferenceEquals(receipt.Task, tail) && receipt.IsStrictPhysical;
        try { await tail.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (!accepted) failure = PhysicalTaskFailureException.CombineRaw(failure, PhysicalTaskFailureException.GetRaw(error));
        }
        if (accepted)
        {
            try { receipt!.Supervisor.CompleteAcceptedRegistration(receipt); }
            catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        }
        // Accepted task outcome remains supervisor-owned. Only distinct registration/fallback
        // failure escapes this caller, after the SAME physical task really completed.
        throw PhysicalTaskFailureException.From(failure!);
    }
}
