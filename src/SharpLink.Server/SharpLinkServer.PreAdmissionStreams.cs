namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private IRpcByteBufferWriter CopyAdmissionPayload(ReadOnlySequence<byte> payload)
    {
        var owner = _runtimeContext.Buffers.Rent(checked((int)payload.Length));
        foreach (var segment in payload)
            owner.Write(segment.Span);
        return owner;
    }

    private void ReservePreAdmissionRequestStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        var admissionController = _admissionController ?? throw new InvalidOperationException(
            "Pre-admission streams require an admission controller.");
        streamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            admissionController.TryReserveAdditionalQueuedBytes,
            admissionController.ReleaseAdditionalQueuedBytes,
            () => callState.TryCancel(
                ServerCallCancellationReason.AdmissionResourceExhausted),
            compressedPayload =>
            {
                var decodedPayload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.StreamData,
                    ProtocolV2FrameFlags.Compressed,
                    compressedPayload,
                    callState.InvocationToken,
                    out var decodedOwner);
                return new PreAdmissionDecodedPayload(
                    decodedPayload.Slice(sizeof(ushort)),
                    decodedOwner ?? throw new InvalidOperationException(
                        "Compressed stream decoding did not return an owner."),
                    _runtimeContext.Buffers);
            });
    }

    private static void DrainRejectedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0 && session.StreamManager is StreamManager streamManager)
            streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private int ResolveRawRequestClientStreamCount(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long contractId) ||
            !reader.TryReadLittleEndian(out long methodId) ||
            !Volatile.Read(ref _services).TryGetValue(contractId, out var registration) ||
            !registration.Stub.TryGetMethodDescriptor(methodId, out var descriptor))
        {
            return 0;
        }

        return descriptor.ClientStreamCount;
    }

    private static void CompleteFailedRequestStreams(
        IRpcSession session,
        long requestId,
        Exception exception)
    {
        if (session.StreamManager is StreamManager streamManager)
            streamManager.CompleteRequestStreams(requestId, exception);
    }

    private static void DrainFailedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

}
