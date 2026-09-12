namespace SharpLink.Server;

internal interface ISharpLinkServerRpcSessionFlushRuntime
{
    SharpLinkRpcSessionFlushPolicySnapshot GetRpcSessionFlushPolicySnapshot();
    void UpdateRpcSessionFlushPolicy(int flushSizeThreshold, TimeSpan maxLatency);
    SharpLinkRuntimeConfigurationUpdateResult TryUpdateRpcSessionFlushPolicy(
        int flushSizeThreshold,
        TimeSpan maxLatency);
}

/// <summary>Runtime RPC session flush controls for <see cref="ISharpLinkServer"/>.</summary>
public static class SharpLinkServerRpcSessionFlushExtensions
{
    /// <summary>Gets the currently published RPC session flush-policy generation.</summary>
    public static SharpLinkRpcSessionFlushPolicySnapshot GetRpcSessionFlushPolicySnapshot(
        this ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server is ISharpLinkServerRpcSessionFlushRuntime runtime
            ? runtime.GetRpcSessionFlushPolicySnapshot()
            : throw new NotSupportedException(
                "This ISharpLinkServer implementation does not expose runtime RPC session flush configuration.");
    }

    /// <summary>
    /// Atomically replaces the flush-size threshold and maximum batching latency used by existing
    /// and future RPC sessions owned by this Server.
    /// </summary>
    public static void UpdateRpcSessionFlushPolicy(
        this ISharpLinkServer server,
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        ArgumentNullException.ThrowIfNull(server);
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        if (server is not ISharpLinkServerRpcSessionFlushRuntime runtime)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime RPC session flush configuration.");
        }
        runtime.UpdateRpcSessionFlushPolicy(flushSizeThreshold, maxLatency);
    }

    /// <summary>
    /// Attempts to atomically publish the RPC session flush policy, returning a structured result for
    /// expected lifecycle or implementation-support rejection.
    /// </summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRpcSessionFlushPolicy(
        this ISharpLinkServer server,
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        ArgumentNullException.ThrowIfNull(server);
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        return server is ISharpLinkServerRpcSessionFlushRuntime runtime
            ? runtime.TryUpdateRpcSessionFlushPolicy(flushSizeThreshold, maxLatency)
            : SharpLinkRuntimeConfigurationUpdateResult.Failure(
                SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
                "This ISharpLinkServer implementation does not support structured runtime RPC session flush configuration.");
    }
}

internal sealed partial class SharpLinkServer : ISharpLinkServerRpcSessionFlushRuntime
{
    SharpLinkRpcSessionFlushPolicySnapshot ISharpLinkServerRpcSessionFlushRuntime.GetRpcSessionFlushPolicySnapshot()
    {
        var current = GetRpcSessionFlushPolicyState().Capture();
        return new SharpLinkRpcSessionFlushPolicySnapshot(
            current.Generation,
            current.FlushSizeThreshold,
            current.MaxLatency);
    }

    void ISharpLinkServerRpcSessionFlushRuntime.UpdateRpcSessionFlushPolicy(
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        lock (_stateGate)
        {
            if (_lifecycle.HasStopStarted)
            {
                throw new InvalidOperationException(
                    $"RPC session flush configuration cannot be updated while the server is {CurrentState}.");
            }
            if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    $"RPC session flush configuration cannot be updated while the server is {CurrentState}.");
            }
            GetRpcSessionFlushPolicyState().Publish(flushSizeThreshold, maxLatency);
        }
    }

    SharpLinkRuntimeConfigurationUpdateResult ISharpLinkServerRpcSessionFlushRuntime.TryUpdateRpcSessionFlushPolicy(
        int flushSizeThreshold,
        TimeSpan maxLatency)
    {
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        lock (_stateGate)
        {
            var state = CurrentState;
            if (_lifecycle.HasStopStarted || IsRuntimeConfigurationPublicationClosed(state))
            {
                return LifecycleClosed(
                    $"RPC session flush configuration cannot be updated while the server is {state}.");
            }

            GetRpcSessionFlushPolicyState().Publish(flushSizeThreshold, maxLatency);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    private RpcSessionFlushPolicyState GetRpcSessionFlushPolicyState()
        => _responseCompressionPolicy.GetOrCreateSessionFlushPolicyState(
            _rpcSessionFlushOptions,
            _runtimeContext.PerformanceProfile);
}
