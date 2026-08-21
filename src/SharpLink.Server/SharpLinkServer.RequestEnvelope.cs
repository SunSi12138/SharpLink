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

    private ServerRequestEnvelope ReadRequestRoutingEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        bool validateMetadataSyntax = false)
    {
        return ServerRequestEnvelopeReader.ReadRouting(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            _runtimeContext.TimeProvider,
            validateMetadataSyntax);
    }

    private ServerRequestEnvelope CompleteDecodedRequestEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        ServerRequestEnvelope routing,
        bool reuseMetadata)
    {
        return ServerRequestEnvelopeReader.CompleteDecoded(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            routing,
            reuseMetadata);
    }

    private bool IsDeadlineExceeded(RpcDeadline deadline)
        => deadline.IsExpired(_runtimeContext.TimeProvider);

}
