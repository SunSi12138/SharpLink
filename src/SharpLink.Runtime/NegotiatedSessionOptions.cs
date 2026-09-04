namespace SharpLink.Runtime;

/// <summary>Immutable result of one successful Protocol v2 handshake.</summary>
internal sealed class NegotiatedSessionOptions
{
    internal NegotiatedSessionOptions(
        ushort protocolMinorVersion,
        ProtocolV2Capabilities capabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        SharpLinkCompressionProviderBinding? compressionBinding = null)
    {
        ProtocolMinorVersion = protocolMinorVersion;
        Capabilities = capabilities;
        MaxFramePayloadBytes = maxFramePayloadBytes;
        StreamReceiveWindowBytes = streamReceiveWindowBytes;
        ConnectionReceiveWindowBytes = connectionReceiveWindowBytes;
        CompressionBinding = compressionBinding;
    }

    internal ushort ProtocolMinorVersion { get; }

    internal ProtocolV2Capabilities Capabilities { get; }

    internal int MaxFramePayloadBytes { get; }

    internal int StreamReceiveWindowBytes { get; }

    internal int ConnectionReceiveWindowBytes { get; }

    internal SharpLinkCompressionProviderBinding? CompressionBinding { get; }
}

internal enum RpcSessionProtocolPhase : byte
{
    Handshaking,
    Ready,
    Draining,
    Stopping,
    Terminal
}

internal static class RpcSessionProtocolRules
{
    internal const ProtocolV2Capabilities RecognizedCapabilities =
        ProtocolV2Capabilities.Metadata |
        ProtocolV2Capabilities.Compression |
        ProtocolV2Capabilities.FlowControl |
        ProtocolV2Capabilities.HealthCheck |
        ProtocolV2Capabilities.CancellationReason;

    internal static bool IsFrameAllowed(
        RpcSessionProtocolPhase phase,
        ProtocolV2FrameType frameType)
        => phase switch
        {
            RpcSessionProtocolPhase.Handshaking =>
                frameType is ProtocolV2FrameType.HandshakeRequest or
                    ProtocolV2FrameType.HandshakeResponse,
            RpcSessionProtocolPhase.Ready =>
                frameType is ProtocolV2FrameType.Ping or
                    ProtocolV2FrameType.Pong or
                    ProtocolV2FrameType.Request or
                    ProtocolV2FrameType.Response or
                    ProtocolV2FrameType.Cancel or
                    ProtocolV2FrameType.StreamData or
                    ProtocolV2FrameType.StreamComplete or
                    ProtocolV2FrameType.WindowUpdate or
                    ProtocolV2FrameType.GoAway or
                    ProtocolV2FrameType.HealthCheck or
                    ProtocolV2FrameType.HealthResponse,
            RpcSessionProtocolPhase.Draining =>
                frameType is ProtocolV2FrameType.Ping or
                    ProtocolV2FrameType.Pong or
                    ProtocolV2FrameType.Response or
                    ProtocolV2FrameType.Cancel or
                    ProtocolV2FrameType.StreamData or
                    ProtocolV2FrameType.StreamComplete or
                    ProtocolV2FrameType.WindowUpdate or
                    ProtocolV2FrameType.GoAway or
                    ProtocolV2FrameType.HealthResponse,
            RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal => false,
            _ => false
        };
}

internal sealed class RpcSessionProtocolState
{
    internal static RpcSessionProtocolState Handshaking { get; } =
        new(RpcSessionProtocolPhase.Handshaking, options: null, flowController: null);

    internal RpcSessionProtocolState(
        RpcSessionProtocolPhase phase,
        NegotiatedSessionOptions? options,
        StreamFlowController? flowController)
    {
        Phase = phase;
        Options = options;
        FlowController = flowController;
    }

    internal RpcSessionProtocolPhase Phase { get; }

    internal NegotiatedSessionOptions? Options { get; }

    internal StreamFlowController? FlowController { get; }

    internal RpcSessionProtocolState WithPhase(RpcSessionProtocolPhase phase)
        => Phase == phase ? this : new RpcSessionProtocolState(phase, Options, FlowController);
}
