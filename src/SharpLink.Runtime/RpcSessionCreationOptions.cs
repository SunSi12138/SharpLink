namespace SharpLink.Runtime;

internal enum RpcSessionRole : byte
{
    Client,
    Server
}

/// <summary>Immutable construction snapshot for one fully configured RPC session.</summary>
internal sealed class RpcSessionCreationOptions
{
    internal RpcSessionCreationOptions(
        RpcSessionRole role,
        SharpLinkRuntimeContext runtimeContext,
        RpcSessionFlushOptions? flushOptions = null,
        CompressionSendPolicyState? compressionSendPolicyState = null,
        bool? writerOwnedStreams = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(runtimeContext);
        if (flushOptions is { } configuredFlushOptions)
        {
            RpcSessionFlushOptions.Validate(
                configuredFlushOptions.FlushSizeThreshold,
                configuredFlushOptions.MaxLatency);
        }

        Role = role;
        RuntimeContext = runtimeContext;
        FlushOptions = flushOptions;
        WriterOwnedStreams = writerOwnedStreams ?? runtimeContext.FlowControl.WriterOwnedStreams;
        CompressionSendPolicyState = compressionSendPolicyState ??
            SharpLink.Runtime.CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        FlushPolicyState = CompressionSendPolicyState.GetOrCreateSessionFlushPolicyState(
            flushOptions,
            runtimeContext.PerformanceProfile);
    }

    internal RpcSessionRole Role { get; }

    internal SharpLinkRuntimeContext RuntimeContext { get; }

    internal RpcSessionFlushOptions? FlushOptions { get; }

    internal CompressionSendPolicyState CompressionSendPolicyState { get; }

    internal RpcSessionFlushPolicyState FlushPolicyState { get; }

    // The runtime snapshot is copied once into each connection. Neither later builder
    // changes nor mutation of an Options clone can switch a live sender's credit ledger.
    internal bool WriterOwnedStreams { get; }

    internal string TelemetrySide => Role == RpcSessionRole.Client ? "client" : "server";
}
