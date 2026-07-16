namespace SharpLink.Abstractions;

/// <summary>Provides instance-scoped runtime services used by generated proxies and stubs.</summary>
public interface IRpcRuntimeContext
{
    /// <summary>Gets the codec provider owned by this client or server.</summary>
    IRpcCodecProvider Codecs { get; }

    /// <summary>Gets the byte-writer pool owned by this client or server.</summary>
    IRpcBufferWriterPool Buffers { get; }
}

/// <summary>Resolves codecs without relying on process-wide mutable configuration.</summary>
public interface IRpcCodecProvider
{
    /// <summary>Gets the codec registered for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The value type to encode or decode.</typeparam>
    /// <returns>The codec bound to this runtime context.</returns>
    IRpcCodec<T> GetCodec<T>();
}

/// <summary>Rents and returns packet writers for one runtime context.</summary>
public interface IRpcBufferWriterPool
{
    /// <summary>Rents a cleared byte writer.</summary>
    ArrayBufferWriter<byte> Rent();

    /// <summary>Returns a writer after its final consumer has finished.</summary>
    /// <param name="writer">The writer whose ownership is returned.</param>
    void Return(ArrayBufferWriter<byte> writer);
}
