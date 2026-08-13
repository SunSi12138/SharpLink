using System;
using System.Buffers;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Server;

/// <summary>
/// Lazy response-payload writer used by the Server success path. It defers renting the pooled
/// packet writer and initial backing buffer until the generated Stub first writes a result or the
/// Server explicitly prepares the response for send after a successful handler completion.
/// </summary>
internal sealed class DeferredRpcResponseWriter : IRpcByteBufferWriter
{
    private readonly SharpLinkBufferWriterPool _pool;
    private readonly RpcSession _session;
    private readonly ProtocolV2FrameType _frameType;
    private readonly ProtocolV2FrameFlags _frameFlags;
    private readonly ulong _requestId;
    private IRpcByteBufferWriter? _inner;
    private PacketToken _packetToken;
    private int _disposed;

    internal DeferredRpcResponseWriter(
        SharpLinkBufferWriterPool pool,
        RpcSession session,
        ProtocolV2FrameType frameType,
        ProtocolV2FrameFlags frameFlags,
        ulong requestId)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _frameType = frameType;
        _frameFlags = frameFlags;
        _requestId = requestId;
    }

    public int WrittenCount { get { ThrowIfDisposed(); return EnsureMaterialized().WrittenCount; } }

    public ReadOnlyMemory<byte> WrittenMemory { get { ThrowIfDisposed(); return EnsureMaterialized().WrittenMemory; } }

    public Span<byte> WrittenSpan { get { ThrowIfDisposed(); return EnsureMaterialized().WrittenSpan; } }

    public int Capacity { get { ThrowIfDisposed(); return EnsureMaterialized().Capacity; } }

    public void Advance(int count)
    {
        ThrowIfDisposed();
        EnsureMaterialized().Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ThrowIfDisposed();
        return EnsureMaterialized().GetMemory(sizeHint);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ThrowIfDisposed();
        return EnsureMaterialized().GetSpan(sizeHint);
    }

    public void Clear()
    {
        ThrowIfDisposed();
        _inner?.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        ReturnIfMaterialized();
    }

    internal IRpcByteBufferWriter PrepareForSend(
        )
    {
        ThrowIfDisposed();
        var writer = EnsureMaterialized();
        writer.EndPacket(_packetToken);
        _inner = null;
        return writer;
    }

    internal void ReturnIfMaterialized()
    {
        var writer = Interlocked.Exchange(ref _inner, null);
        if (writer is not null)
            _pool.Return(writer);
    }

    private IRpcByteBufferWriter EnsureMaterialized()
    {
        while (true)
        {
            ThrowIfDisposed();
            var existing = Volatile.Read(ref _inner);
            if (existing is not null)
                return existing;

            var rented = _session.RentFrameWriter();
            rented.BeginPacket(_frameType, _frameFlags, _requestId);
            _packetToken = new PacketToken(0);
            var winner = Interlocked.CompareExchange(ref _inner, rented, null);
            if (winner is null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (Interlocked.CompareExchange(ref _inner, null, rented) == rented)
                        _pool.Return(rented);
                    throw new ObjectDisposedException(nameof(DeferredRpcResponseWriter));
                }
                return rented;
            }
            _pool.Return(rented);
            return winner;
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
