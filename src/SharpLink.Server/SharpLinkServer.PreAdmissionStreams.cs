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
        RpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0)
            return;

        var streamManager = session.StreamManager;
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

    private void ReservePreInvocationRequestStreams(
        RpcSession session,
        int clientStreamCount,
        long requestId,
        CancellationToken cancellationToken,
        bool retainUntilLocalCompletion = false)
    {
        if (clientStreamCount == 0)
            return;

        var streamManager = session.StreamManager;
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload> decodeCompressed = compressedPayload =>
        {
            var decodedPayload = session.DecodeInboundPayload(
                ProtocolV2FrameType.StreamData,
                ProtocolV2FrameFlags.Compressed,
                compressedPayload,
                cancellationToken,
                out var decodedOwner);
            return new PreAdmissionDecodedPayload(
                decodedPayload.Slice(sizeof(ushort)),
                decodedOwner ?? throw new InvalidOperationException(
                    "Compressed stream decoding did not return an owner."),
                _runtimeContext.Buffers);
        };

        if (session.HasStreamFlowControl)
        {
            // Negotiated receive credit already bounds bytes retained while the interceptor is
            // suspended. If admission already owns the route, this registration only promotes
            // that wrapper out of queue-byte accounting and may add OneWay local retention.
            streamManager.ReservePreAdmissionStreams(
                requestId,
                clientStreamCount,
                _runtimeContext.Buffers,
                static _ => true,
                static _ => { },
                static () => { },
                decodeCompressed,
                retainUntilLocalCompletion);
            return;
        }

        // FlowControl is optional. Without negotiated receive credit, keep the temporary
        // pre-invocation route independently byte-bounded instead of relying only on the 4096
        // element cap. Allow at least one legal maximum-size frame so the local safety bound does
        // not make a valid peer frame impossible solely because the configured stream window is
        // smaller than the negotiated frame limit.
        var maxRetainedBytes = Math.Max(
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            session.NegotiatedMaxFramePayloadBytes);
        for (var index = 1; index <= clientStreamCount; index++)
        {
            var streamId = checked((ushort)index);
            var retention = new ActivePreInvocationStreamRetention(maxRetainedBytes);
            streamManager.Register(
                requestId,
                streamId,
                new PreAdmissionStreamDispatcher(
                    _runtimeContext.Buffers,
                    retention.TryReserve,
                    retention.Release,
                    () => streamManager.CompleteStream(
                        requestId,
                        streamId,
                        new SharpLinkException(
                            SharpLinkErrorCode.ResourceExhausted,
                            $"Deferred client-stream retention exceeded the {maxRetainedBytes}-byte limit without negotiated flow control.")),
                    decodeCompressed,
                    retainUntilLocalCompletion));
        }
    }

    private static void DrainRejectedOneWayStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0)
            session.StreamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private static void DrainCompletedOneWayStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0)
            session.StreamManager.AbandonExistingRequestStreams(requestId, clientStreamCount);
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

internal sealed class ActivePreInvocationStreamRetention
{
    private readonly int _maxRetainedBytes;
    private int _retainedBytes;

    internal ActivePreInvocationStreamRetention(int maxRetainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedBytes);
        _maxRetainedBytes = maxRetainedBytes;
    }

    internal int RetainedBytes => Volatile.Read(ref _retainedBytes);

    internal bool TryReserve(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        while (true)
        {
            var current = Volatile.Read(ref _retainedBytes);
            if (bytes > _maxRetainedBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _retainedBytes, current + bytes, current) == current)
                return true;
        }
    }

    internal void Release(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var remaining = Interlocked.Add(ref _retainedBytes, -bytes);
        if (remaining < 0)
            throw new InvalidOperationException("Deferred client-stream retention accounting became negative.");
    }
}
