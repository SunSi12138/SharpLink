namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        ServerConnectionState connection, long requestId,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        RpcDeadline resolvedDeadline = default)
    {
        var request = ServerRequestEnvelopeReader.Read(
            connection.Session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            _runtimeContext.TimeProvider,
            resolvedDeadline);
        var binding = connection.Bindings.GetRequest(requestId);
        if (request.BindingToken != binding.Token || request.MethodHash != binding.Method.MethodId)
            throw ContractBindingWire.Violation("Request changed its captured binding identity.");
        return request with { Binding = binding };
    }

    private bool IsDeadlineExceeded(RpcDeadline deadline)
        => deadline.IsExpired(_runtimeContext.TimeProvider);

}
