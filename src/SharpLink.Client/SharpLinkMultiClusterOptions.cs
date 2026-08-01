namespace SharpLink.Client;

/// <summary>Configures global resource limits for a multi-cluster client.</summary>
public sealed class SharpLinkMultiClusterOptions
{
    /// <summary>Gets or sets the maximum number of configured cluster slots.</summary>
    public int MaxClusters { get; set; } = 16;

    /// <summary>Gets or sets the total configured connection budget across all slots.</summary>
    public int MaxTotalConfiguredConnections { get; set; } = 64;

    /// <summary>Gets or sets the maximum number of slot connection attempts running concurrently.</summary>
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
