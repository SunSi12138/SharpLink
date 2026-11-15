namespace SharpLink.Client;

// Failure-time, control-only snapshots. No business bytes, authentication, Codec, Type or exception roots.
internal sealed record ContractBindingOperationTrace(ulong Operation, long ContractId, string Phase,
    TaskStatus PreviousPublication, TaskStatus? Send, bool Published, TaskStatus Ack, int? AckStatus,
    TaskStatus Result, long RemainingTicks, string? FailureType, SharpLinkErrorCode? FailureCode, ushort? FailureDetail);
internal sealed record ContractBindingTargetTrace(string Session, long SessionGeneration, ulong Token);
internal sealed record ContractBindingSessionTrace(string Session, long SessionGeneration, RpcSessionProtocolPhase ProtocolPhase,
    RpcSessionSupportSnapshot Support, bool SendPumpIdleWait, bool Connected, bool CanAcceptCalls, bool Closed, ulong LastIssued, int Reservations, int MaintenanceOperations,
    TaskStatus PublicationTail, ContractBindingOperationTrace[] Operations, ulong[] ConfirmedTokens,
    ContractBindingOperationTrace? FirstFailedOperation, string? CloseReasonType, SharpLinkErrorCode? CloseCode, ushort? CloseDetail);
internal sealed record ContractBindingSiteTrace(long LocalGeneration, long ContractId, bool LocalAvailable,
    bool Committed, bool Running, bool Dirty, bool Stopped, string RefreshPhase, int CapturedConnections, int QualifiedConnections, string? ErrorType,
    SharpLinkErrorCode? ErrorCode, ushort? ErrorDetail, ContractBindingTargetTrace[] Targets);
internal sealed record ContractBindingClientTrace(SharpLinkClientReadinessSnapshot Readiness,
    string StopPhase, string? StoppingSession, TaskStatus? ConnectTask, TaskStatus? StopTask,
    ContractBindingDiagnosticSnapshot Ledger, ContractBindingSessionTrace[] ReadySessions,
    ContractBindingSessionTrace[] ClosedSessions, ContractBindingSiteTrace[] Sites,
    FrameworkTaskSupervisorSnapshot FrameworkTasks);

internal sealed partial class SharpLinkClient
{
    private string _bindingStopPhase = "NotStarted";
    private string? _bindingStopSession;
    private void TraceBindingStop(string phase, string? session = null)
    {
        if (!BindingTraceEnabled) return;
        Volatile.Write(ref _bindingStopSession, session); Volatile.Write(ref _bindingStopPhase, phase);
    }
    private bool _bindingTraceEnabled;
    internal bool BindingTraceEnabled => Volatile.Read(ref _bindingTraceEnabled);
    internal void EnableContractBindingTraceForDiagnostics() => Volatile.Write(ref _bindingTraceEnabled, true);
    private readonly Lock _bindingTraceGate = new();
    // Keep the first three closure causes and the latest closure. Reconnect cannot grow this history.
    private readonly List<ContractBindingSessionTrace> _closedBindingTraces = [];
    internal void RecordBindingClosure(ContractBindingSessionTrace trace)
    {
        lock (_bindingTraceGate)
        {
            if (_closedBindingTraces.Count < 4) _closedBindingTraces.Add(trace);
            else _closedBindingTraces[3] = trace;
        }
    }
    internal ContractBindingClientTrace CaptureContractBindingTrace()
    {
        ContractBindingSessionTrace[] closed;
        lock (_bindingTraceGate) closed = [.. _closedBindingTraces];
        ContractBindingSiteTrace[] sites;
        lock (_contractSiteGate)
            sites = _contractSites.Values.Select(weak => weak.TryGetTarget(out var site) ? site.CaptureTrace() : null)
                .OfType<ContractBindingSiteTrace>().ToArray();
        return new(GetReadinessSnapshot(), Volatile.Read(ref _bindingStopPhase), Volatile.Read(ref _bindingStopSession),
            _connectTask?.Status, _stopTask?.Status, CaptureContractBindingDiagnostics(),
            CaptureBindingConnections().Select(static connection => connection.Bindings.CaptureTrace()).ToArray(),
            closed, sites, FrameworkTaskSnapshotForDiagnostics);
    }
}
