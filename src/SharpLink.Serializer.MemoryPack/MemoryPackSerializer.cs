using System.Buffers;
using MemoryPack;
using SharpLink.Abstractions;

namespace SharpLink.Runtime;

public class MemoryPackSerializerAdaptor : ISerializer
{
    public void Serialize<T>(in T value, IBufferWriter<byte> writer)
    {
        MemoryPackSerializer.Serialize(writer, value);
    }

    public T? Deserialize<T>(ref ReadOnlySequence<byte> sequence)
    {
        return MemoryPackSerializer.Deserialize<T>(sequence);
    }
}
        
    
