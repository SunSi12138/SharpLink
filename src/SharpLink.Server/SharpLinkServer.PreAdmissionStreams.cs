namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
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
            resourceGovernor.TryReservePreAdmissionStreamBytes,
            resourceGovernor.ReleasePreAdmissionStreamBytes,
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

    /// <summary>
    /// Pins the physical retained buffer across an asynchronous consumer. Dispose may be requested
    /// while a use is active; the buffer is returned only after the final use releases it.
    /// </summary>
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
            // The physical retained buffer is returned before its accounting permit is
            // released. If the permit was transferred to a decode owner, this Dispose is
            // intentionally a no-op and CompleteDecode performs the accounting release.
            _pool.Return(_owner);
        }
        finally
        {
            _retainedPermit?.Dispose();
        }
    }
}
