namespace SharpLink.Server;

/// <summary>
/// Captures the counters observed by the single winner immediately before it
/// publishes server call drain completion. This is internal test diagnostics
/// and is never captured on the normal request hot path.
/// </summary>
internal readonly record struct ServerCallDrainSignalSnapshot(
    int GlobalActiveCalls,
    int PendingAdmissions,
    int ReleasingConnectionActiveCalls);
