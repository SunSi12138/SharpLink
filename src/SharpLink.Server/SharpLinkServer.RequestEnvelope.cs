namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        RpcDeadline resolvedDeadline = default)
    {
        return ServerRequestEnvelopeReader.Read(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            _runtimeContext.TimeProvider,
            resolvedDeadline);
    }

    private bool IsDeadlineExceeded(RpcDeadline deadline)
        => deadline.IsExpired(_runtimeContext.TimeProvider);

}
