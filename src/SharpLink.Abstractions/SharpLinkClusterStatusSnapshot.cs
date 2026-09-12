namespace SharpLink.Abstractions;

/// <summary>Captures one point-in-time status projection for a configured multi-cluster slot.</summary>
public readonly record struct SharpLinkClusterStatusSnapshot
{
    /// <summary>Creates a status snapshot from one observed child-client connection state.</summary>
    /// <param name="cluster">The configured cluster key represented by this snapshot.</param>
    /// <param name="connectionState">The observed child-client connection state.</param>
    /// <exception cref="ArgumentException"><paramref name="cluster"/> is the default or otherwise invalid.</exception>
    public SharpLinkClusterStatusSnapshot(
        SharpLinkClusterKey cluster,
        SharpLinkConnectionState connectionState)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
        {
            throw new ArgumentException(
                "A valid non-default SharpLinkClusterKey is required.",
                nameof(cluster));
        }

        Cluster = cluster;
        ConnectionState = connectionState;
    }

    /// <summary>Gets the cluster key represented by this snapshot.</summary>
    public SharpLinkClusterKey Cluster { get; }

    /// <summary>Gets the legacy connection-oriented state observed for the cluster slot.</summary>
    public SharpLinkConnectionState ConnectionState { get; }

    /// <summary>Gets the connectivity state projected from <see cref="ConnectionState"/>.</summary>
    public SharpLinkClusterState RuntimeState => ConnectionState switch
    {
        SharpLinkConnectionState.Created => SharpLinkClusterState.Inactive,
        SharpLinkConnectionState.Connecting => SharpLinkClusterState.Connecting,
        SharpLinkConnectionState.Ready => SharpLinkClusterState.Ready,
        SharpLinkConnectionState.Draining => SharpLinkClusterState.Draining,
        SharpLinkConnectionState.Reconnecting => SharpLinkClusterState.Reconnecting,
        SharpLinkConnectionState.Stopped => SharpLinkClusterState.Stopped,
        _ => SharpLinkClusterState.Unavailable
    };

    /// <summary>Gets readiness projected from <see cref="ConnectionState"/>.</summary>
    public SharpLinkReadinessState Readiness => ConnectionState == SharpLinkConnectionState.Ready
        ? SharpLinkReadinessState.Ready
        : SharpLinkReadinessState.NotReady;
}
