namespace SharpLink.Server;

/// <summary>Runtime active-RPC capacity operations for SharpLink servers.</summary>
public static class SharpLinkServerCallCapacityExtensions
{
    /// <summary>
    /// Atomically replaces the per-connection and server-wide active RPC limits used by future
    /// call acquisitions. Existing active calls are never cancelled solely because either limit
    /// is reduced.
    /// </summary>
    /// <param name="server">The server whose hard active-RPC limits are updated.</param>
    /// <param name="maxConcurrentCallsPerConnection">Maximum active calls on one connection.</param>
    /// <param name="maxConcurrentCallsPerServer">Maximum active calls across the server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either candidate limit is outside its supported range.</exception>
    /// <exception cref="InvalidOperationException">The server is stopping or has stopped/faulted.</exception>
    /// <exception cref="NotSupportedException">The server implementation does not support runtime call-capacity updates.</exception>
    public static void UpdateCallCapacity(
        this ISharpLinkServer server,
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server is not ISharpLinkServerCallCapacityRuntimeControl runtimeControl)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime call-capacity updates.");
        }

        runtimeControl.UpdateCallCapacity(
            maxConcurrentCallsPerConnection,
            maxConcurrentCallsPerServer);
    }
}

internal interface ISharpLinkServerCallCapacityRuntimeControl
{
    void UpdateCallCapacity(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer);
}
