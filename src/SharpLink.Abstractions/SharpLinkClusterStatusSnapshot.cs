namespace SharpLink.Abstractions;

/// <summary>Captures immutable status values observed for one configured multi-cluster slot.</summary>
public readonly record struct SharpLinkClusterStatusSnapshot
{
    /// <summary>Creates a status snapshot from independently observed child-client state domains.</summary>
    /// <param name="cluster">The configured cluster key represented by this snapshot.</param>
    /// <param name="connectionState">The observed legacy child-client connection state.</param>
    /// <param name="runtimeState">The observed canonical child-client cluster runtime state.</param>
    /// <param name="readiness">The observed canonical child-client readiness state.</param>
    /// <exception cref="ArgumentException"><paramref name="cluster"/> is the default or otherwise invalid.</exception>
    public SharpLinkClusterStatusSnapshot(
        SharpLinkClusterKey cluster,
        SharpLinkConnectionState connectionState,
        SharpLinkClusterState runtimeState,
        SharpLinkReadinessState readiness)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
        {
            throw new ArgumentException(
                "A valid non-default SharpLinkClusterKey is required.",
                nameof(cluster));
        }

        Cluster = cluster;
        ConnectionState = connectionState;
        RuntimeState = runtimeState;
        Readiness = readiness;
    }

    /// <summary>Gets the cluster key represented by this snapshot.</summary>
    public SharpLinkClusterKey Cluster { get; }

    /// <summary>Gets the legacy connection-oriented state observed for the cluster slot.</summary>
    public SharpLinkConnectionState ConnectionState { get; }

    /// <summary>Gets the canonical connectivity state observed for the cluster runtime.</summary>
    public SharpLinkClusterState RuntimeState { get; }

    /// <summary>Gets the canonical readiness observed for the cluster slot.</summary>
    public SharpLinkReadinessState Readiness { get; }
}
