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
        ServerConnectionState connection,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState,
        bool retainUntilLocalCompletion = false)
    {
        if (clientStreamCount == 0)
            return;

        ReservePreAdmissionRequestStreamsCore(connection, requestId, clientStreamCount, callState, retainUntilLocalCompletion);
    }

    private void ReservePreAdmissionRequestStreamsCore(
        ServerConnectionState connection,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState,
        bool retainUntilLocalCompletion)
    {
        var streamManager = connection.Session.StreamManager;
        var resourceGovernor = ResourceGovernor;
        var cancellationToken = callState.InvocationToken;
        var callLease = callState.CaptureLease(requestId);
        streamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            retainedBytes => resourceGovernor.TryReservePreAdmissionStreamBytes(retainedBytes),
            retainedBytes => resourceGovernor.ReleasePreAdmissionStreamBytes(retainedBytes),
            () =>
            {
                if (!callLease.TryAcquire())
                    return;
                try { callLease.State.TryCancel(ServerCallCancellationReason.PreAdmissionStreamResourceExhausted); }
                finally { callLease.ReleaseUse(); }
            },
            payload => DecodeDeferredInput(connection, requestId, payload, cancellationToken),
            retainUntilLocalCompletion,
            retainUntilLocalCompletion ? callLease.TerminalOwner : default);
    }

    private void ReservePreInvocationRequestStreams(
        ServerConnectionState connection,
        int clientStreamCount,
        long requestId,
        CancellationToken cancellationToken,
        StreamTerminalOwner retainedInputOwner = default)
    {
        if (clientStreamCount == 0)
            return;

        // Keep captured callback state inside the streaming-only method. A lambda in
        // this scope would allocate its display class before the no-stream return.
        ReservePreInvocationRequestStreamsCore(
            connection, clientStreamCount, requestId, cancellationToken, retainedInputOwner);
    }

    private void ReservePreInvocationRequestStreamsCore(
        ServerConnectionState connection,
        int clientStreamCount,
        long requestId,
        CancellationToken cancellationToken,
        StreamTerminalOwner retainedInputOwner)
    {
        var session = connection.Session;
        var streamManager = session.StreamManager;
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload> decodeCompressed =
            payload => DecodeDeferredInput(connection, requestId, payload, cancellationToken);

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
                retainUntilLocalCompletion: retainedInputOwner.IsPresent,
                retainedInputOwner: retainedInputOwner);
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
                    retainUntilLocalCompletion: retainedInputOwner.IsPresent,
                    maxRetainedBytes: maxRetainedBytes,
                    retainedInputOwner: retainedInputOwner));
        }
    }

    // Raw items in a stable mailbox cannot reach this decoder until the typed
    // invocation attaches. Provider execution can race the original terminal.
    private static PreAdmissionDecodedPayload DecodeDeferredInput(
        ServerConnectionState connection, long requestId, ReadOnlySequence<byte> payload, CancellationToken token)
    {
        var calls = connection.CallCancellations;
        if (!TryAcceptInboundStreamProgress(calls, requestId, out var owned, out _) || !owned)
            return default;

        var session = connection.Session;
        IRpcByteBufferWriter? owner = null;
        try
        {
            var decoded = session.DecodeInboundPayload(
                ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.Compressed, payload, token, out owner);
            if (!TryAcceptInboundStreamProgress(calls, requestId, out owned, out _) || !owned)
                return default;
            var result = new PreAdmissionDecodedPayload(decoded.Slice(sizeof(ushort)),
                owner ?? throw new InvalidOperationException("Compressed stream decode returned no owner."),
                session.RuntimeContext.Buffers);
            owner = null;
            return result;
        }
        catch (OperationCanceledException) when (
            !TryAcceptInboundStreamProgress(calls, requestId, out var stillOwned, out _) || !stillOwned)
        {
            return default;
        }
        finally
        {
            session.ReturnDecodedPayload(owner);
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

    private static int ResolveRejectedNStudyOneWayCount(
        ServerConnectionState connection, ReadOnlySequence<byte> payload, int declaredCount)
    {
        if (declaredCount != 0)
            return declaredCount;
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long bindingId) ||
            !reader.TryReadLittleEndian(out long methodId))
        {
            return UnresolvedClientStreamCount;
        }

        return connection.NStudyBindings.IsRetired(bindingId) ||
            (connection.NStudyBindings.TryResolve(bindingId, out var registration) &&
             registration.TryGetNStudyMethod(methodId, out var facts) &&
             facts.Descriptor.Kind == RpcMethodKind.OneWay && !facts.Descriptor.HasClientStreams)
            ? 0 : UnresolvedClientStreamCount;
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
