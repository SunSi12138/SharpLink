using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IPhysicalTailAudit : IService
{
    IAsyncEnumerable<TailAuditItem> WarmupAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<TailAuditItem> ServerAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<TailAuditItem> DuplexAsync(IAsyncEnumerable<int> input, CancellationToken cancellationToken);
}

[RpcCodec(typeof(TailAuditCodec))]
public readonly record struct TailAuditItem(int Value);

[RpcCodecSemanticIdentity(0x967dad1849184c24UL, 0x9914e2c035800af1UL)]
public sealed class TailAuditCodec : IRpcCodec<TailAuditItem>, IDisposable
{
    public static int Created;
    public static int Disposed;
    public static int Serialized;
    public static int Deserialized;
    public TailAuditCodec() => Interlocked.Increment(ref Created);
    public void Serialize(in TailAuditItem value, IBufferWriter<byte> buffer)
    {
        Interlocked.Increment(ref Serialized);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(4), value.Value);
        buffer.Advance(4);
    }
    public TailAuditItem Deserialize(in ReadOnlySequence<byte> buffer)
    {
        Interlocked.Increment(ref Deserialized);
        Span<byte> bytes = stackalloc byte[4];
        buffer.CopyTo(bytes);
        return new(BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }
    public void Dispose() => Interlocked.Increment(ref Disposed);
}
