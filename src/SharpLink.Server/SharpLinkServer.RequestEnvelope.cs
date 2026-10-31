namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        RpcDeadline resolvedDeadline = default,
        ProtocolV2FrameType frameType = ProtocolV2FrameType.Request)
    {
        return ServerRequestEnvelopeReader.Read(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            _runtimeContext.TimeProvider,
            resolvedDeadline,
            frameType);
    }

    private bool IsDeadlineExceeded(RpcDeadline deadline)
        => deadline.IsExpired(_runtimeContext.TimeProvider);

}
