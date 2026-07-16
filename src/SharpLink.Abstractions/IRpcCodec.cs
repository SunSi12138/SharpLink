namespace SharpLink.Abstractions;

public interface IRpcCodec
{
}

public interface IRpcCodec<T>:IRpcCodec
{
    /// <summary>Serializes a value to a sequential byte writer.</summary>
    void Serialize(in T value, IBufferWriter<byte> buffer);
    
    T? Deserialize(in ReadOnlySequence<byte> buffer);
}
