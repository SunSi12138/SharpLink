namespace SharpLink.Server;

/// <summary>
/// Captures a stop-time, best-effort view of calls that outlived the configured grace period.
/// This is internal test diagnostics and is never populated on the normal request hot path.
/// </summary>
internal sealed record ServerStopDiagnosticSnapshot(
    DateTimeOffset CapturedUtc,
    int GlobalActiveCalls,
    IReadOnlyList<ServerConnectionDiagnosticSnapshot> Connections);

internal sealed record ServerConnectionDiagnosticSnapshot(
    string SessionId,
    string LifecycleState,
    int ActiveCalls,
    int ActiveStreams,
    IReadOnlyList<ServerCallDiagnosticSnapshot> Calls);

internal sealed record ServerCallDiagnosticSnapshot(
    long RequestId,
    string CancellationReason,
    DateTimeOffset? Deadline,
    long DeadlineTimestamp);
