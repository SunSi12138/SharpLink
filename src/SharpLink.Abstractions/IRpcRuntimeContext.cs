using System.Reflection;

namespace SharpLink.Abstractions;

/// <summary>Provides instance-scoped runtime services used by generated proxies and stubs.</summary>
public interface IRpcRuntimeContext
{
    /// <summary>Gets the codec provider owned by this client or server.</summary>
    IRpcCodecProvider Codecs { get; }

    /// <summary>Gets the byte-writer pool owned by this client or server.</summary>
    IRpcBufferWriterPool Buffers { get; }
}

/// <summary>Resolves the immutable Codec provider owned by one generated RPC Contract assembly.</summary>
public interface IRpcContractCodecProviderResolver
{
    /// <summary>Gets the Codec provider bound to <paramref name="ownerAssembly"/>.</summary>
    IRpcCodecProvider GetContractCodecProvider(Assembly ownerAssembly);
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
    IRpcByteBufferWriter Rent();

    /// <summary>Rents a cleared writer that rejects growth beyond an ownership-specific byte limit.</summary>
    /// <param name="maxWrittenBytes">Maximum bytes that may be advanced during this lease.</param>
    /// <returns>A bounded writer lease owned by the caller until it is returned.</returns>
    /// <remarks>Protocol implementations should use this overload for every network frame.</remarks>
    IRpcByteBufferWriter Rent(int maxWrittenBytes);

    /// <summary>Returns a writer after its final consumer has finished.</summary>
    /// <param name="writer">The writer whose ownership is returned.</param>
    void Return(IRpcByteBufferWriter writer);
}

/// <summary>
/// Represents an owned, contiguous RPC packet buffer. Codecs should depend only on
/// <see cref="IBufferWriter{T}"/>; the additional members exist for protocol header backfilling.
/// </summary>
public interface IRpcByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    /// <summary>Gets the number of bytes written to the current lease.</summary>
    int WrittenCount { get; }

    /// <summary>Gets the current written bytes without transferring ownership.</summary>
    ReadOnlyMemory<byte> WrittenMemory { get; }

    /// <summary>Gets the mutable written region used by the protocol framing layer.</summary>
    Span<byte> WrittenSpan { get; }

    /// <summary>Gets the capacity of the current array lease.</summary>
    int Capacity { get; }

    /// <summary>Clears the written region while retaining the active lease.</summary>
    void Clear();
}
