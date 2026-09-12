namespace SharpLink.Abstractions;

/// <summary>Marks a serializer that can be registered for an RPC contract type.</summary>
public interface IRpcCodec
{
}

/// <summary>Serializes and deserializes values of <typeparamref name="T"/> on the RPC wire.</summary>
/// <typeparam name="T">The contract value type.</typeparam>
public interface IRpcCodec<T> : IRpcCodec
{
    /// <summary>Serializes a value to a sequential byte writer.</summary>
    void Serialize(in T value, IBufferWriter<byte> buffer);

    /// <summary>Deserializes one value from a complete encoded payload.</summary>
    /// <param name="buffer">The encoded payload.</param>
    /// <returns>The decoded value.</returns>
    T? Deserialize(in ReadOnlySequence<byte> buffer);
}
