using System.Buffers;
using MemoryPack;
using SharpLink.Abstractions;

namespace SharpLink.Runtime;

public sealed class MemoryPackSerializerAdaptor : ISerializer
{
    public void Serialize<T>(in T value, IBufferWriter<byte> writer)
    {
        MemoryPackSerializer.Serialize(in writer, value);
    }
    
    public T? Deserialize<T>(ref ReadOnlySequence<byte> sequence)
    {
        return MemoryPackSerializer.Deserialize<T>(sequence);
    }
}
