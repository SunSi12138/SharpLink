namespace SharpLink.Client;

internal interface ISharpLinkClientRpcSessionFlushRuntime
{
    SharpLinkRpcSessionFlushPolicySnapshot GetRpcSessionFlushPolicySnapshot();
    void UpdateRpcSessionFlushPolicy(int flushSizeThreshold, TimeSpan maxLatency);
}

/// <summary>Runtime RPC session flush controls for <see cref="ISharpLinkClient"/>.</summary>
public static class SharpLinkClientRpcSessionFlushExtensions
{
    /// <summary>Gets the currently published RPC session flush-policy generation.</summary>
    public static SharpLinkRpcSessionFlushPolicySnapshot GetRpcSessionFlushPolicySnapshot(
        this ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is ISharpLinkClientRpcSessionFlushRuntime runtime
            ? runtime.GetRpcSessionFlushPolicySnapshot()
            : throw new NotSupportedException(
                "This ISharpLinkClient implementation does not expose runtime RPC session flush configuration.");
    }

    /// <summary>
    /// Atomically replaces the flush-size threshold and maximum batching latency used by existing
    /// and future RPC sessions owned by this Client.
    /// </summary>
    public static void UpdateRpcSessionFlushPolicy(
        this ISharpLinkClient client,
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        ArgumentNullException.ThrowIfNull(client);
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        if (client is not ISharpLinkClientRpcSessionFlushRuntime runtime)
        {
            throw new NotSupportedException(
                "This ISharpLinkClient implementation does not support runtime RPC session flush configuration.");
        }
        runtime.UpdateRpcSessionFlushPolicy(flushSizeThreshold, maxLatency);
    }
}

internal sealed partial class SharpLinkClient : ISharpLinkClientRpcSessionFlushRuntime
{
    SharpLinkRpcSessionFlushPolicySnapshot ISharpLinkClientRpcSessionFlushRuntime.GetRpcSessionFlushPolicySnapshot()
    {
        var current = GetRpcSessionFlushPolicyState().Capture();
        return new SharpLinkRpcSessionFlushPolicySnapshot(
            current.Generation,
            current.FlushSizeThreshold,
            current.MaxLatency);
    }

    void ISharpLinkClientRpcSessionFlushRuntime.UpdateRpcSessionFlushPolicy(
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"RPC session flush configuration cannot be updated while the client is {state}.");
            }
            GetRpcSessionFlushPolicyState().Publish(flushSizeThreshold, maxLatency);
        }
    }

    private RpcSessionFlushPolicyState GetRpcSessionFlushPolicyState()
        => _requestCompressionPolicy.GetOrCreateSessionFlushPolicyState(
            _rpcSessionFlushOptions,
            _runtimeContext.PerformanceProfile);
}
