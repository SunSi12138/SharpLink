namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal RpcSessionSupportSnapshot CaptureSupportSnapshot()
    {
        var protocolState = Volatile.Read(ref _protocolState);
        var negotiated = protocolState.Options;
        int queuedBytes;
        int queueLimitBytes;
        lock (_pumpGate)
        {
            var pump = _pump;
            queuedBytes = pump is null ? 0 : Volatile.Read(ref pump._queuedBytes);
            queueLimitBytes = pump is null ? _runtimeContext.FlowControl.MaxSendQueueBytes : pump._maxQueuedBytes;
        }

        var security = _transport as ITransportSecurityInfo;
        return new RpcSessionSupportSnapshot(
            protocolState.Phase,
            negotiated?.ProtocolMinorVersion,
            negotiated?.Capabilities,
            negotiated?.CompressionBinding is not null,
            negotiated?.MaxFramePayloadBytes,
            negotiated?.StreamReceiveWindowBytes,
            negotiated?.ConnectionReceiveWindowBytes,
            queuedBytes,
            queueLimitBytes,
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
