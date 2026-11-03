namespace SharpLink.Client;

internal enum ChildAssemblyDisposition : byte
{
    RejectedWithoutSelection,
    SelectedAborted,
    SelectedCommitted,
    JoinedUncommittedStage,
    JoinedExistingCommitted
}

// One caller-prepaid synchronous selection receipt. These are the ORIGINAL child
// roots, not a fabricated Task standing in for a rejected or failed invocation.
// Only the child registry writes selection fields; Finalize runs outside its gate.
internal sealed class ChildAssemblySelectionMailbox(Action claim, Action<ChildAssemblySelectionMailbox> finalize)
{
    private bool _finalized;
    private bool _joined;
    internal RetirementControl? Control { get; private set; }
    internal Task<SharpLinkAssemblyUnregisterResult>? Physical { get; private set; }
    internal Task<(Exception? Failure, bool SupervisorOwned)>? Observation { get; private set; }
    internal ChildAssemblyDisposition Disposition { get; private set; }
    internal Exception? FinalizationFailure { get; private set; }
    internal bool IsFinalized => _finalized;
    internal bool ReturnedFailureIsSourceProjection { get; private set; }
    internal Exception? InvocationFailure { get; private set; }
    internal void DeclareReturnedSourceProjection() => ReturnedFailureIsSourceProjection = true;
    // A decorator's independent stage is data, even when it throws the SAME raw
    // object as an original physical failure. It is never reference-deduplicated.
    internal void RecordInvocationFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (InvocationFailure is not null) throw new InvalidOperationException("Invocation outcome already recorded.");
        InvocationFailure = failure;
    }

    internal void Claim() => claim();
    internal void Select(RetirementControl control, bool joined = false)
    {
        if (Control is not null) throw new InvalidOperationException("The exact child stage was already selected.");
        Control = control;
        _joined = joined;
    }
    internal void CaptureRoots()
    {
        if (Control is null) return;
        Physical ??= Control.Module.RetirementOperation;
        Observation ??= Control.Module.RetirementObservation;
    }
    internal void FinalizeDisposition()
    {
        if (_finalized) return;
        CaptureRoots();
        Disposition = Control is null ? ChildAssemblyDisposition.RejectedWithoutSelection :
            _joined ? (Control.IsCommitted ? ChildAssemblyDisposition.JoinedExistingCommitted :
                ChildAssemblyDisposition.JoinedUncommittedStage) :
            Control.IsCommitted ? ChildAssemblyDisposition.SelectedCommitted : ChildAssemblyDisposition.SelectedAborted;
        _finalized = true;
        // The sealed coordinator handoff puts mandatory swap/release in finally.
        // Preserve its independent failure, never skip child rollback/activation.
        try { finalize(this); }
        catch (Exception failure) { FinalizationFailure = failure; }
    }

    internal bool HasPhysicalExit => Physical is { IsCompleted: true } &&
        Observation is { IsCompleted: true } && Control is { PhysicalExited: true };
    internal bool ModuleReleased => Control?.Module.State == SharpLinkDynamicModuleState.Released;
    internal bool AccountingRefunded => Control is { CapacityRefunded: true };
    internal bool AbortCertified => Control is { IsCommitted: false, ReservationLeaseReturned: true } &&
        Physical is { IsCompletedSuccessfully: true } && Observation is { IsCompletedSuccessfully: true } &&
        Observation.Result.Failure is null && HasPhysicalExit;
}
