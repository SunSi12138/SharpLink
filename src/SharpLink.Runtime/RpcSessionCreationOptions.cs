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
        bool writerOwnedStreams = false)
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
        WriterOwnedStreams = writerOwnedStreams;
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

    // Internal, immutable opt-in while the production adapters are being validated.
    // Never switch the send-credit authority after a connection has started publishing.
    internal bool WriterOwnedStreams { get; }

    internal string TelemetrySide => Role == RpcSessionRole.Client ? "client" : "server";
}
