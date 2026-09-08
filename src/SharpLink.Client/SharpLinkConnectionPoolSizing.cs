namespace SharpLink.Client;

/// <summary>Identifies which existing Client topology owns the published connection-capacity limits.</summary>
public enum SharpLinkConnectionPoolSizingKind : byte
{
    /// <summary>A fixed/single-endpoint connection pool with MinConnections and MaxConnections.</summary>
    FixedEndpoint = 0,

    /// <summary>A static or resolver-backed endpoint cluster with total and per-endpoint hard limits.</summary>
    EndpointCluster = 1
}

/// <summary>Describes the currently published Client connection-capacity generation.</summary>
public readonly record struct SharpLinkConnectionPoolSizingSnapshot(
    ulong Generation,
    SharpLinkConnectionPoolSizingKind Kind,
    int MinConnections,
    int MaxConnections,
    int MaxConnectionsPerEndpoint);

internal interface ISharpLinkConnectionPoolSizingRuntime
{
    SharpLinkConnectionPoolSizingSnapshot GetConnectionPoolSizingSnapshot();
    void UpdateFixedConnectionPoolSizing(int minConnections, int maxConnections);
    void UpdateClusterConnectionPoolSizing(int maxConnections, int maxConnectionsPerEndpoint);
}

/// <summary>Runtime connection-capacity controls for <see cref="ISharpLinkClient"/>.</summary>
public static class SharpLinkConnectionPoolSizingExtensions
{
    /// <summary>Gets the currently published fixed or endpoint-cluster connection-capacity generation.</summary>
    public static SharpLinkConnectionPoolSizingSnapshot GetConnectionPoolSizingSnapshot(this ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is ISharpLinkConnectionPoolSizingRuntime runtime
            ? runtime.GetConnectionPoolSizingSnapshot()
            : throw new NotSupportedException(
                "This ISharpLinkClient implementation does not expose runtime connection-pool sizing.");
    }

    /// <summary>
    /// Atomically replaces a fixed endpoint pool's minimum target and hard maximum. Increasing the minimum
    /// converges through the existing reconnect lifecycle; decreasing the maximum gracefully drains surplus connections.
    /// </summary>
    public static void UpdateFixedConnectionPoolSizing(
        this ISharpLinkClient client,
        int minConnections,
        int maxConnections)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateFixed(minConnections, maxConnections);
        if (client is not ISharpLinkConnectionPoolSizingRuntime runtime)
            throw new NotSupportedException("This ISharpLinkClient implementation does not support runtime connection-pool sizing.");
        runtime.UpdateFixedConnectionPoolSizing(minConnections, maxConnections);
    }

    /// <summary>
    /// Atomically replaces an endpoint cluster's total and per-endpoint hard connection limits. Surplus
    /// live connections stop accepting new calls and retire after their existing work drains.
    /// </summary>
    public static void UpdateClusterConnectionPoolSizing(
        this ISharpLinkClient client,
        int maxConnections,
        int maxConnectionsPerEndpoint)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateCluster(maxConnections, maxConnectionsPerEndpoint);
        if (client is not ISharpLinkConnectionPoolSizingRuntime runtime)
            throw new NotSupportedException("This ISharpLinkClient implementation does not support runtime connection-pool sizing.");
        runtime.UpdateClusterConnectionPoolSizing(maxConnections, maxConnectionsPerEndpoint);
    }

    internal static void ValidateFixed(int minConnections, int maxConnections)
    {
        if (minConnections is < 1 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(minConnections));
        if (maxConnections is < 1 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (maxConnections < minConnections)
            throw new ArgumentException("maxConnections cannot be smaller than minConnections.", nameof(maxConnections));
    }

    internal static void ValidateCluster(int maxConnections, int maxConnectionsPerEndpoint)
    {
        if (maxConnections is < 1 or > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (maxConnectionsPerEndpoint < 1 || maxConnectionsPerEndpoint > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerEndpoint));
    }
}
