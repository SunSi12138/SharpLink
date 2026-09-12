namespace SharpLink.Abstractions;

/// <summary>Creates manifest-scoped Codec state for one serializer integration.</summary>
public interface IRpcCodecAdapter
{
    /// <summary>Gets the implementation and lifecycle identity.</summary>
    string AdapterId { get; }

    /// <summary>Creates isolated state for one runtime Context and generated manifest.</summary>
    IRpcCodecAdapterScope CreateScope();
}

/// <summary>Creates closed Codecs that share one serializer context.</summary>
public interface IRpcCodecAdapterScope : IDisposable
{
    /// <summary>Creates a Codec for one source-generated closed payload type.</summary>
    IRpcCodec<T> CreateCodec<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>();
}
