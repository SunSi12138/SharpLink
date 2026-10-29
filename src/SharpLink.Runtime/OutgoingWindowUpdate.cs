namespace SharpLink.Runtime;

/// <summary>
/// Owns one trusted outgoing credit until the send pump flushes or drains it.
/// Its payload is immutable between rent and return; it never aggregates credits.
/// </summary>
internal sealed class OutgoingWindowUpdate(OutgoingWindowUpdatePool pool)
{
    internal const int PayloadBytes = sizeof(ushort) + sizeof(uint);
    internal const int FrameBytes = ProtocolV2Constants.HeaderBytes + PayloadBytes;

    private readonly OutgoingWindowUpdatePool _pool = pool;
    private long _requestId;
    private int _credit;
    private ushort _streamId;
    internal OutgoingWindowUpdate? Next;

    internal void Initialize(long requestId, ushort streamId, int credit)
    {
        _requestId = requestId;
        _streamId = streamId;
        _credit = credit;
    }

    internal void WriteTo(Span<byte> destination)
    {
        destination = destination[..FrameBytes];
        destination[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], PayloadBytes);
        destination[5] = (byte)ProtocolV2FrameType.WindowUpdate;
        destination[6] = (byte)ProtocolV2FrameFlags.None;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[7..], unchecked((ulong)_requestId));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ProtocolV2Constants.HeaderBytes..], _streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[(ProtocolV2Constants.HeaderBytes + sizeof(ushort))..], checked((uint)_credit));
    }

    internal void Return()
    {
        _requestId = 0;
        _streamId = 0;
        _credit = 0;
        _pool.Return(this);
    }
}

/// <summary>
/// Runtime-context-wide bounded storage, not a per-connection retention budget.
/// The gate protects only free nodes and never spans queue, output, or callbacks.
/// </summary>
internal sealed class OutgoingWindowUpdatePool : IDisposable
{
    internal const int MaximumRetainedNodes = 512;
    private readonly Lock _gate = new();
    private OutgoingWindowUpdate? _head;
    private int _count;
    private bool _disposed;

    internal OutgoingWindowUpdate Rent(long requestId, ushort streamId, int credit)
    {
        OutgoingWindowUpdate? update;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            update = _head;
            if (update is not null)
            {
                _head = update.Next;
                update.Next = null;
                _count--;
            }
        }
        update ??= new OutgoingWindowUpdate(this);
        update.Initialize(requestId, streamId, credit);
        return update;
    }

    internal void Return(OutgoingWindowUpdate update)
    {
        lock (_gate)
        {
            if (_disposed || _count >= MaximumRetainedNodes)
                return;
            update.Next = _head;
            _head = update;
            _count++;
        }
    }

    internal int RetainedCount
    {
        get { lock (_gate) return _count; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _head = null;
            _count = 0;
        }
    }
}
