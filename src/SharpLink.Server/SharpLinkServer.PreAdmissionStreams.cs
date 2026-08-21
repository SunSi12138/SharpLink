namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private long _retainedRequestPayloadCopies;

    private IRpcByteBufferWriter CopyAdmissionPayload(ReadOnlySequence<byte> payload)
    {
        Interlocked.Increment(ref _retainedRequestPayloadCopies);
        var owner = _runtimeContext.Buffers.Rent(checked((int)payload.Length));
        foreach (var segment in payload)
            owner.Write(segment.Span);
        return owner;
    }

    private void ReservePreAdmissionRequestStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0)
            return;

        var admissionController = _admissionController ?? throw new InvalidOperationException(
            "Pre-admission streams require an admission controller.");
        ReserveBufferedRequestStreams(
            session,
            requestId,
            clientStreamCount,
            callState,
            admissionController.TryReserveAdditionalQueuedBytes,
            admissionController.ReleaseAdditionalQueuedBytes);
    }

    private void ReservePreDecodeRequestStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0)
            return;

        // The call slot is already owned before this temporary buffer is installed. Keep the
        // reader loop free to consume Cancel/StreamData while synchronous request decompression
        // runs elsewhere, but cap retained stream bytes to the negotiated connection receive
        // window so a slow provider cannot create an unbounded handoff queue.
        var budget = new PreDecodeStreamBufferBudget(
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes);
        ReserveBufferedRequestStreams(
            session,
            requestId,
            clientStreamCount,
            callState,
            budget.TryReserve,
            budget.Release);
    }

    private void ReserveBufferedRequestStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState,
        Func<int, bool> reserveBytes,
        Action<int> releaseBytes)
    {
        session.StreamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            reserveBytes,
            releaseBytes,
            () => callState.TryCancel(
                ServerCallCancellationReason.AdmissionResourceExhausted),
            compressedPayload =>
            {
                var decodedPayload = session.DecodeInboundPayload(
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

    private sealed class PreDecodeStreamBufferBudget(int capacity)
    {
        private int _remaining = capacity;

        internal bool TryReserve(int bytes)
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (bytes > remaining)
                    return false;
                if (Interlocked.CompareExchange(
                        ref _remaining,
                        remaining - bytes,
                        remaining) == remaining)
                {
                    return true;
                }
            }
        }

        internal void Release(int bytes) => Interlocked.Add(ref _remaining, bytes);
    }

    private static void DrainRejectedOneWayStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0)
            session.StreamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
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
        RpcSession session,
        long requestId,
        Exception exception)
    {
        session.StreamManager.CompleteRequestStreams(requestId, exception);
    }

    private static void DrainFailedOneWayStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount == 0)
            return;

        session.StreamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

}
