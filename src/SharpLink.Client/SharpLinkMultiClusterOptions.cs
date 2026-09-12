namespace SharpLink.Client;

/// <summary>Configures global resource limits for a multi-cluster client.</summary>
public sealed class SharpLinkMultiClusterOptions
{
    /// <summary>Gets or sets the maximum number of configured cluster slots.</summary>
    public int MaxClusters { get; set; } = 16;

    /// <summary>Gets or sets the total configured connection budget across all slots.</summary>
    public int MaxTotalConfiguredConnections { get; set; } = 64;

    /// <summary>
    /// Gets or sets the maximum number of physical child transport connection attempts that may run concurrently.
    /// </summary>
    /// <remarks>
    /// The limit is shared across all cluster slots and applies at the
    /// <see cref="IClientTransportFactory.ConnectAsync(CancellationToken)"/> boundary, including initial dials,
    /// reconnects, connection-pool expansion, runtime Add/Replace candidates, and dynamic endpoint generations.
    /// Established connections do not consume a permit. Coordinator startup/compatibility fan-out may also use
    /// this value as an orchestration bound, but callers may rely on the physical transport-attempt limit itself.
    /// </remarks>
    public int MaxConcurrentClusterConnects { get; set; } = 4;

    internal SharpLinkMultiClusterOptions CloneValidated()
    {
        if (MaxClusters is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxClusters));
        if (MaxTotalConfiguredConnections is < 1 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalConfiguredConnections));
        if (MaxConcurrentClusterConnects is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentClusterConnects));

        return new SharpLinkMultiClusterOptions
        {
            MaxClusters = MaxClusters,
            MaxTotalConfiguredConnections = MaxTotalConfiguredConnections,
            MaxConcurrentClusterConnects = MaxConcurrentClusterConnects
        };
    }
}

/// <summary>Configures one cluster slot in a multi-cluster client.</summary>
public sealed class SharpLinkMultiClusterSlotOptions
{
    /// <summary>Gets or sets whether a slot without a static contract route may accept dynamic contracts.</summary>
    public bool AllowDynamicContracts { get; set; }
}
