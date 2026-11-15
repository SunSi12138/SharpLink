namespace SharpLink.Client;

// Failure-time, control-only snapshots. Unknown/contended sections are never reported as zero.
internal sealed record ContractBindingOperationTrace(ulong Operation, long ContractId, string Phase,
    TaskStatus PreviousPublication, TaskStatus? Send, bool Published, TaskStatus Ack, int? AckStatus,
    TaskStatus Result, long DeadlineTimestamp, string? FailureType, SharpLinkErrorCode? FailureCode, ushort? FailureDetail);
internal sealed record ContractBindingSessionSample(string Session, long SessionGeneration, NonBlockingDiagnostic<ContractBindingSessionTrace> Snapshot);
internal sealed record ContractBindingSiteSample(long LocalGeneration, long ContractId, NonBlockingDiagnostic<ContractBindingSiteTrace> Snapshot);
internal sealed record ContractBindingTargetTrace(string Session, long SessionGeneration, ulong Token);
internal sealed record ContractBindingSessionTrace(string Session, long SessionGeneration, RpcSessionProtocolPhase ProtocolPhase,
    long SendQueuedBytes, bool SendPumpIdleWait, bool Connected, bool CanAcceptCalls, bool Closed, ulong LastIssued, int Reservations, int MaintenanceOperations,
    TaskStatus PublicationTail, ContractBindingOperationTrace[] Operations, ulong[] ConfirmedTokens,
    ContractBindingOperationTrace? FirstFailedOperation, string? CloseReasonType, SharpLinkErrorCode? CloseCode, ushort? CloseDetail);
internal sealed record ContractBindingSiteTrace(long LocalGeneration, long ContractId, bool LocalAvailable,
    bool Committed, bool Running, bool Dirty, bool Stopped, string RefreshPhase, int CapturedConnections, int QualifiedConnections, string? ErrorType,
    SharpLinkErrorCode? ErrorCode, ushort? ErrorDetail, ContractBindingTargetTrace[] Targets);
internal sealed record ContractBindingLedgerTrace(int Bindings, int Operations, int PrepareWaiters,
    int? DemandSites, int? PublishedTargets, int DroppedClosures, int DiagnosticFailures);
internal sealed record ContractBindingClientTrace(SharpLinkClientReadinessSnapshot Readiness,
    string StopPhase, string? StoppingSession, TaskStatus? ConnectTask, TaskStatus? StopTask,
    ContractBindingLedgerTrace Ledger,
    NonBlockingDiagnostic<ContractBindingSessionSample[]> ReadySessions,
    NonBlockingDiagnostic<ContractBindingSessionSample[]> ClosedSessions,
    NonBlockingDiagnostic<ContractBindingSiteSample[]> Sites,
    NonBlockingDiagnostic<FrameworkTaskSupervisorSnapshot> FrameworkTasks);

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
    private int _droppedBindingClosures, _bindingDiagnosticFailures;
    // Keep the first three closure samples and the latest one. No sample retains the live Session.
    private readonly List<ContractBindingSessionSample> _closedBindingTraces = [];
    internal void RecordBindingDiagnosticFailure() => Interlocked.Increment(ref _bindingDiagnosticFailures);
    internal void RecordBindingClosure(ContractBindingSessionSample trace)
    {
        if (!_bindingTraceGate.TryEnter()) { Interlocked.Increment(ref _droppedBindingClosures); return; }
        try
        {
            if (_closedBindingTraces.Count < 4) _closedBindingTraces.Add(trace);
            else _closedBindingTraces[3] = trace;
        }
        finally { _bindingTraceGate.Exit(); }
    }
    // Pure contention control: holds the actual index gate, not a simulated routing table.
    internal void HoldContractSiteIndexForDiagnosticTest(Action body)
    {
        if (!_contractSiteGate.TryEnter()) throw new InvalidOperationException("Diagnostic test gate is already held.");
        try { body(); } finally { _contractSiteGate.Exit(); }
    }
    private sealed record SiteIndexTrace(int Count, ContractSite[] Sites);
    internal ContractBindingClientTrace CaptureContractBindingTrace()
    {
        var closed = NonBlockingDiagnostic<ContractBindingSessionSample[]>.Capture(
            _bindingTraceGate, () => _closedBindingTraces.ToArray());
        var index = NonBlockingDiagnostic<SiteIndexTrace>.Capture(_contractSiteGate, () => new(_contractSites.Count,
            _contractSites.Values.Select(weak => weak.TryGetTarget(out var site) ? site : null).OfType<ContractSite>().ToArray()));
        var capturedSites = index.Value?.Sites;
        var sites = capturedSites is not null
            ? NonBlockingDiagnostic<ContractBindingSiteSample[]>.Available(capturedSites.Select(static site => site.CaptureTraceNonBlocking()).ToArray())
            : new(index.State, null, index.FailureType);
        var connections = _cluster?.CaptureReadyConnectionsNonBlocking()
            ?? NonBlockingDiagnostic<ClientConnection[]>.Available(Volatile.Read(ref _readyConnections));
        var sessions = connections.Value is { } ready
            ? NonBlockingDiagnostic<ContractBindingSessionSample[]>.Available(ready.Select(static connection => connection.Bindings.CaptureTraceNonBlocking()).ToArray())
            : new(connections.State, null, connections.FailureType);
        int? publishedTargets = sites.Value is { } sampled && sampled.All(static site => site.Snapshot.Value is not null)
            ? sampled.Sum(static site => site.Snapshot.Value!.Targets.Length) : null;
        return new(GetReadinessSnapshot(), Volatile.Read(ref _bindingStopPhase), Volatile.Read(ref _bindingStopSession),
            _connectTask?.Status, _stopTask?.Status,
            new(_contractBindingQuota.Bindings, _contractBindingQuota.Operations, Volatile.Read(ref _prepareWaiters),
                index.Value?.Count, publishedTargets, Volatile.Read(ref _droppedBindingClosures), Volatile.Read(ref _bindingDiagnosticFailures)),
            sessions, closed, sites, _frameworkTasks.CaptureSnapshotNonBlocking());
    }
}
