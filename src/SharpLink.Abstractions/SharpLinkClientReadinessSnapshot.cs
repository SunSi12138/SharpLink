namespace SharpLink.Abstractions;

/// <summary>
/// Describes an immutable point-in-time observation of one Client's active endpoint topology.
/// The topology can change immediately after the snapshot is returned; the value is not a lease or
/// a guarantee that the observed readiness level will be retained.
/// </summary>
/// <param name="State">The current Client lifecycle state.</param>
/// <param name="ActiveEndpoints">
/// The number of endpoints in the active routing topology currently accepted and owned by the Client.
/// Retired dynamic generations and old draining endpoints are excluded.
/// </param>
/// <param name="ReadyEndpoints">The number of active endpoints with at least one ready connection.</param>
/// <param name="ReadyConnections">The total number of ready connections across active ready endpoints.</param>
/// <param name="TargetReadyEndpoints">The convergence target for the currently active topology.</param>
public readonly record struct SharpLinkClientReadinessSnapshot(
    SharpLinkConnectionState State,
    int ActiveEndpoints,
    int ReadyEndpoints,
    int ReadyConnections,
    int TargetReadyEndpoints)
{
    /// <summary>
    /// Gets whether this point-in-time observation is in the Ready lifecycle state and satisfies the
    /// configured convergence target with at least one ready connection.
    /// </summary>
    public bool MeetsTarget =>
        State == SharpLinkConnectionState.Ready &&
        TargetReadyEndpoints > 0 &&
        ReadyConnections > 0 &&
        ReadyEndpoints >= TargetReadyEndpoints;
}
