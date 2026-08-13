namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags)
    {
        return ServerRequestEnvelopeReader.Read(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            _runtimeContext.TimeProvider);
    }

    private bool IsDeadlineExceeded(RpcDeadline deadline)
        => deadline.IsExpired(_runtimeContext.TimeProvider);

}
