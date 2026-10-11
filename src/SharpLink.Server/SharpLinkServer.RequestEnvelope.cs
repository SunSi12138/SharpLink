namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        RpcDeadline resolvedDeadline = default)
    {
        if ((flags & ProtocolV2FrameFlags.HasContractIdentity) == 0 ||
            (session.NegotiatedCapabilities & ProtocolV2Capabilities.ContractAdmission) == 0)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "Every request must carry a negotiated contract identity.");
        }
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
