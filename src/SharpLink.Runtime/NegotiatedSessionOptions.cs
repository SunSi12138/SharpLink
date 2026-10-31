namespace SharpLink.Runtime;

/// <summary>Immutable result of one successful Protocol3 handshake.</summary>
internal sealed class NegotiatedSessionOptions
{
    internal NegotiatedSessionOptions(
        ushort protocolMinorVersion,
        ProtocolV2Capabilities capabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        SharpLinkCompressionProviderBinding? compressionBinding = null,
        Protocol3BindingAuthority authority = default)
    {
        ProtocolMinorVersion = protocolMinorVersion;
        Capabilities = capabilities;
        MaxFramePayloadBytes = maxFramePayloadBytes;
        StreamReceiveWindowBytes = streamReceiveWindowBytes;
        ConnectionReceiveWindowBytes = connectionReceiveWindowBytes;
        CompressionBinding = compressionBinding;
        Authority = authority;
    }

    internal Protocol3BindingAuthority Authority { get; }

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
        ProtocolV2Capabilities.CancellationReason |
        ProtocolV2Capabilities.ContractManifest |
        ProtocolV2Capabilities.SessionRefresh;

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
                    ProtocolV2FrameType.HealthResponse or
                    ProtocolV2FrameType.ContractManifest or
                    ProtocolV2FrameType.ResponseCompressionPreferenceUpdate or
                    ProtocolV2FrameType.ResponseCompressionPreferenceAck or
                    ProtocolV2FrameType.SessionRefreshRequested or
                    ProtocolV2FrameType.BindContract or
                    ProtocolV2FrameType.BindResult or
                    ProtocolV2FrameType.BindingInvalidated or
                    ProtocolV2FrameType.ReleaseBinding or
                    ProtocolV2FrameType.CancelBind or
                    ProtocolV2FrameType.RequestAccepted or
                    ProtocolV2FrameType.OneWayStreamRequest,
            // Receiving these request frames permits bounded refusal, never new call admission.
            // A progress-class GoAway may overtake requests already published to the normal queue.
            RpcSessionProtocolPhase.Draining =>
                frameType is ProtocolV2FrameType.Request or ProtocolV2FrameType.Ping or
                    ProtocolV2FrameType.Pong or
                    ProtocolV2FrameType.Response or
                    ProtocolV2FrameType.Cancel or
                    ProtocolV2FrameType.StreamData or
                    ProtocolV2FrameType.StreamComplete or
                    ProtocolV2FrameType.WindowUpdate or
                    ProtocolV2FrameType.GoAway or
                    ProtocolV2FrameType.HealthResponse or
                    ProtocolV2FrameType.ResponseCompressionPreferenceUpdate or
                    ProtocolV2FrameType.ResponseCompressionPreferenceAck or
                    ProtocolV2FrameType.SessionRefreshRequested or
                    ProtocolV2FrameType.BindContract or
                    ProtocolV2FrameType.BindResult or
                    ProtocolV2FrameType.BindingInvalidated or
                    ProtocolV2FrameType.ReleaseBinding or
                    ProtocolV2FrameType.CancelBind or
                    ProtocolV2FrameType.RequestAccepted or
                    ProtocolV2FrameType.OneWayStreamRequest,
            RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal => false,
            _ => false
        };

    internal static bool IsInboundDirectionAllowed(RpcSessionRole role, ProtocolV2FrameType type)
        => type switch
        {
            ProtocolV2FrameType.HandshakeRequest or ProtocolV2FrameType.Request or
            ProtocolV2FrameType.OneWayStreamRequest or ProtocolV2FrameType.BindContract or
            ProtocolV2FrameType.ReleaseBinding or ProtocolV2FrameType.CancelBind or
            ProtocolV2FrameType.HealthCheck or ProtocolV2FrameType.ResponseCompressionPreferenceUpdate
                => role == RpcSessionRole.Server,
            ProtocolV2FrameType.HandshakeResponse or ProtocolV2FrameType.Response or
            ProtocolV2FrameType.BindResult or ProtocolV2FrameType.BindingInvalidated or
            ProtocolV2FrameType.RequestAccepted or ProtocolV2FrameType.HealthResponse or
            ProtocolV2FrameType.ContractManifest or ProtocolV2FrameType.ResponseCompressionPreferenceAck or
            ProtocolV2FrameType.SessionRefreshRequested
                => role == RpcSessionRole.Client,
            ProtocolV2FrameType.Ping or ProtocolV2FrameType.Pong or ProtocolV2FrameType.Cancel or
            ProtocolV2FrameType.StreamData or ProtocolV2FrameType.StreamComplete or
            ProtocolV2FrameType.WindowUpdate or ProtocolV2FrameType.GoAway => true,
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
