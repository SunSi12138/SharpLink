namespace SharpLink.Abstractions;

public interface IRpcCodec
{
}

public interface IRpcCodec<T>:IRpcCodec
{
    void Serialize(in T value, in ArrayBufferWriter<byte> buffer);
    
    T? Deserialize(in ReadOnlySequence<byte> buffer);
}