namespace SharpLink.Client;

/// <summary>Configures the bounded resources owned by a static multi-endpoint client.</summary>
public sealed class SharpLinkClusterOptions
{
    /// <summary>The maximum number of endpoints accepted by one static topology.</summary>
    public const int MaximumEndpoints = 64;

    private long _connectionLimits = PackConnectionLimits(4, 2);

    /// <summary>Gets or sets the maximum number of endpoints. Values must be from one through 64.</summary>
    public int MaxEndpoints { get; set; } = MaximumEndpoints;

    /// <summary>Gets or sets the target number of endpoints with at least one Ready connection.</summary>
    public int MinReadyEndpoints { get; set; } = 2;

    /// <summary>Gets or sets the global Ready and Connecting connection budget.</summary>
    public int MaxConnections
    {
        get => UnpackMaxConnections(Volatile.Read(ref _connectionLimits));
        set => UpdateBuilderConnectionLimits(maxConnections: value, maxConnectionsPerEndpoint: null);
    }

    /// <summary>Gets or sets the maximum number of Ready and Connecting connections per endpoint.</summary>
    public int MaxConnectionsPerEndpoint
    {
        get => UnpackMaxConnectionsPerEndpoint(Volatile.Read(ref _connectionLimits));
        set => UpdateBuilderConnectionLimits(maxConnections: null, maxConnectionsPerEndpoint: value);
    }

    /// <summary>Gets or sets the separate maximum number of retiring connections.</summary>
    public int MaxRetiringConnections { get; set; } = 4;

    internal SharpLinkClusterOptions CloneValidated(int endpointCount)
    {
        var limits = Volatile.Read(ref _connectionLimits);
        var maxConnections = UnpackMaxConnections(limits);
        var maxConnectionsPerEndpoint = UnpackMaxConnectionsPerEndpoint(limits);
        if (endpointCount is < 2 or > MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(endpointCount));
        if (MaxEndpoints is < 1 or > MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(MaxEndpoints));
        if (endpointCount > MaxEndpoints)
            throw new ArgumentException("The configured endpoint collection exceeds MaxEndpoints.", nameof(endpointCount));
        if (MinReadyEndpoints is < 1 or > MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(MinReadyEndpoints));
        if (maxConnections is < 1 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (maxConnectionsPerEndpoint < 1 || maxConnectionsPerEndpoint > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnectionsPerEndpoint));
        if (Math.Min(MinReadyEndpoints, endpointCount) > maxConnections)
            throw new ArgumentException("MinReadyEndpoints cannot exceed MaxConnections.", nameof(MinReadyEndpoints));
        if (MaxRetiringConnections is < 0 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxRetiringConnections));

        var clone = new SharpLinkClusterOptions
        {
            MaxEndpoints = MaxEndpoints,
            MinReadyEndpoints = MinReadyEndpoints,
            MaxRetiringConnections = MaxRetiringConnections
        };
        clone.PublishRuntimeConnectionLimits(maxConnections, maxConnectionsPerEndpoint);
        return clone;
    }

    internal SharpLinkClusterOptions CloneValidatedForDynamicResolver()
    {
        var limits = Volatile.Read(ref _connectionLimits);
        var maxConnections = UnpackMaxConnections(limits);
        var maxConnectionsPerEndpoint = UnpackMaxConnectionsPerEndpoint(limits);
        if (MaxEndpoints is < 1 or > MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(MaxEndpoints));
        if (MinReadyEndpoints is < 1 or > MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(MinReadyEndpoints));
        if (maxConnections is < 1 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (maxConnectionsPerEndpoint < 1 || maxConnectionsPerEndpoint > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnectionsPerEndpoint));
        if (Math.Min(MinReadyEndpoints, MaxEndpoints) > maxConnections)
            throw new ArgumentException("MinReadyEndpoints cannot exceed MaxConnections.", nameof(MinReadyEndpoints));
        if (MaxRetiringConnections is < 0 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxRetiringConnections));

        var clone = new SharpLinkClusterOptions
        {
            MaxEndpoints = MaxEndpoints,
            MinReadyEndpoints = MinReadyEndpoints,
            MaxRetiringConnections = MaxRetiringConnections
        };
        clone.PublishRuntimeConnectionLimits(maxConnections, maxConnectionsPerEndpoint);
        return clone;
    }

    internal void PublishRuntimeConnectionLimits(int maxConnections, int maxConnectionsPerEndpoint)
        => Interlocked.Exchange(ref _connectionLimits, PackConnectionLimits(maxConnections, maxConnectionsPerEndpoint));

    private void UpdateBuilderConnectionLimits(int? maxConnections, int? maxConnectionsPerEndpoint)
    {
        while (true)
        {
            var current = Volatile.Read(ref _connectionLimits);
            var next = PackConnectionLimits(
                maxConnections ?? UnpackMaxConnections(current),
                maxConnectionsPerEndpoint ?? UnpackMaxConnectionsPerEndpoint(current));
            if (Interlocked.CompareExchange(ref _connectionLimits, next, current) == current)
                return;
        }
    }

    private static long PackConnectionLimits(int maxConnections, int maxConnectionsPerEndpoint)
        => unchecked((long)((ulong)(uint)maxConnectionsPerEndpoint << 32 | (uint)maxConnections));

    private static int UnpackMaxConnections(long limits) => unchecked((int)(uint)limits);
    private static int UnpackMaxConnectionsPerEndpoint(long limits)
        => unchecked((int)(uint)((ulong)limits >> 32));
}

/// <summary>Chooses the built-in strategy used for static endpoint selection.</summary>
public enum SharpLinkLoadBalancingStrategy
{
    /// <summary>Compares two random Ready endpoints by active calls per Ready connection.</summary>
    PowerOfTwoChoices,

    /// <summary>Chooses one random Ready endpoint.</summary>
    Random,

    /// <summary>Cycles through Ready endpoints with an instance-scoped atomic cursor.</summary>
    RoundRobin,

    /// <summary>Scans Ready endpoints for the least pending work with rotating tie breaking.</summary>
    LeastPending
}
