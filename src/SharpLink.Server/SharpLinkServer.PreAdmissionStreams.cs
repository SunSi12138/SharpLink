namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int UnresolvedClientStreamCount = -1;

    private bool TryCopyAdmissionPayload(
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        out ServerRetainedAdmissionPayload? retainedPayload)
    {
        retainedPayload = null;
        ServerRetainedCompressedPermit? retainedPermit = null;
        var isCompressed = (flags & ProtocolV2FrameFlags.Compressed) != 0;
        if (isCompressed &&
            !ResourceGovernor.TryAcquireRetained(payload.Length, out retainedPermit))
        {
            return false;
        }

        IRpcByteBufferWriter? owner = null;
        try
        {
            owner = _runtimeContext.Buffers.Rent(checked((int)payload.Length));
            foreach (var segment in payload)
                owner.Write(segment.Span);
            retainedPayload = new ServerRetainedAdmissionPayload(
                _runtimeContext.Buffers,
                owner,
                retainedPermit);
            owner = null;
            retainedPermit = null;
            return true;
        }
        finally
        {
            if (owner is not null)
                _runtimeContext.Buffers.Return(owner);
            retainedPermit?.Dispose();
        }
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
        var resourceGovernor = ResourceGovernor;
        streamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            retainedBytes => resourceGovernor.TryReservePreAdmissionStreamBytes(retainedBytes),
            retainedBytes => resourceGovernor.ReleasePreAdmissionStreamBytes(retainedBytes),
            () => callState.TryCancel(
                ServerCallCancellationReason.PreAdmissionStreamResourceExhausted),
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
            // Negotiated receive credit is the byte bound. Reconfiguration changes only future
            // external accounting/decoder state; already-buffered owners keep their original
            // admission release callback until replay/discard releases them.
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

        // Without negotiated receive credit, the stable mailbox itself owns the active byte cap.
        // Existing admission-buffered bytes are already included in that mailbox count, so moving
        // into invocation does not re-reserve/rewrite their resource owner. If they already exceed
        // the active cap, reconfiguration marks the mailbox terminal and releases them exactly once.
        var maxRetainedBytes = Math.Max(
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            session.NegotiatedMaxFramePayloadBytes);
        for (var index = 1; index <= clientStreamCount; index++)
        {
            var streamId = checked((ushort)index);
            streamManager.Register(
                requestId,
                streamId,
                new PreAdmissionStreamDispatcher(
                    _runtimeContext.Buffers,
                    static _ => true,
                    static _ => { },
                    () => streamManager.CompleteStream(
                        requestId,
                        streamId,
                        new SharpLinkException(
                            SharpLinkErrorCode.ResourceExhausted,
                            $"Deferred client-stream retention exceeded the {maxRetainedBytes}-byte limit without negotiated flow control.")),
                    decodeCompressed,
                    retainUntilLocalCompletion,
                    maxRetainedBytes));
        }
    }

    private static void DrainRejectedOneWayStreams(
        RpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount == UnresolvedClientStreamCount)
        {
            _ = TerminateUnresolvableOneWayRequest(session, requestId);
            return;
        }

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
            return UnresolvedClientStreamCount;
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

internal sealed class ServerRetainedAdmissionPayload : IDisposable
{
    private readonly SharpLinkBufferWriterPool _pool;
    private readonly IRpcByteBufferWriter _owner;
    private readonly ServerRetainedCompressedPermit? _retainedPermit;
    private readonly Lock _lifetimeGate = new();
    private int _activeUses;
    private bool _disposeRequested;
    private bool _released;

    internal ServerRetainedAdmissionPayload(
        SharpLinkBufferWriterPool pool,
        IRpcByteBufferWriter owner,
        ServerRetainedCompressedPermit? retainedPermit)
    {
        _pool = pool;
        _owner = owner;
        _retainedPermit = retainedPermit;
    }

    internal ReadOnlySequence<byte> Payload
    {
        get
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                return new ReadOnlySequence<byte>(_owner.WrittenMemory);
            }
        }
    }

    internal ServerRetainedCompressedPermit? RetainedPermit
    {
        get
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                return _retainedPermit;
            }
        }
    }

    internal void AcquireUse()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested || _released, this);
            _activeUses++;
        }
    }

    internal void ReleaseUse()
    {
        var release = false;
        lock (_lifetimeGate)
        {
            if (--_activeUses < 0)
            {
                _activeUses++;
                throw new InvalidOperationException("Retained admission payload use count underflowed.");
            }
            if (_disposeRequested && _activeUses == 0 && !_released)
            {
                _released = true;
                release = true;
            }
        }
        if (release)
            ReleaseCore();
    }

    public void Dispose()
    {
        var release = false;
        lock (_lifetimeGate)
        {
            if (_disposeRequested)
                return;
            _disposeRequested = true;
            if (_activeUses == 0 && !_released)
            {
                _released = true;
                release = true;
            }
        }
        if (release)
            ReleaseCore();
    }

    private void ReleaseCore()
    {
        try
        {
            _pool.Return(_owner);
        }
        finally
        {
            _retainedPermit?.Dispose();
        }
    }
}
