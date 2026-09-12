namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal RpcSessionSupportSnapshot CaptureSupportSnapshot()
    {
        var protocolState = Volatile.Read(ref _protocolState);
        var negotiated = protocolState.Options;
        var security = _transport as ITransportSecurityInfo;
        return new RpcSessionSupportSnapshot(
            protocolState.Phase,
            negotiated?.ProtocolMinorVersion,
            negotiated?.Capabilities,
            negotiated?.CompressionBinding is not null,
            negotiated?.MaxFramePayloadBytes,
            negotiated?.StreamReceiveWindowBytes,
            negotiated?.ConnectionReceiveWindowBytes,
            checked((int)Math.Min(QueuedSendBytes, int.MaxValue)),
            RuntimeContext.FlowControl.MaxSendQueueBytes,
            StreamManager.ActiveStreamCount,
            security is not null,
            security?.Protocol.ToString(),
            security?.CipherSuite.ToString());
    }
}

internal readonly record struct RpcSessionSupportSnapshot(
    RpcSessionProtocolPhase ProtocolPhase,
    ushort? ProtocolMinorVersion,
    ProtocolV2Capabilities? Capabilities,
    bool CompressionNegotiated,
    int? MaxFramePayloadBytes,
    int? StreamReceiveWindowBytes,
    int? ConnectionReceiveWindowBytes,
    int SendQueuedBytes,
    int SendQueueLimitBytes,
    int ActiveStreams,
    bool Tls,
    string? TlsProtocol,
    string? CipherSuite);
