namespace SharpLink.Abstractions;

/// <summary>Creates one source-generated Codec for an immutable runtime-context snapshot.</summary>
public interface IRpcGeneratedCodecFactory
{
    /// <summary>Gets the closed DTO or collection type handled by the factory.</summary>
    Type TargetType { get; }

    /// <summary>Gets the deterministic identity of the finalized Codec semantics.</summary>
    RpcHash128 CodecHash => default;

    /// <summary>Gets the adapter lifecycle identity, or null for adapter-free Codecs.</summary>
    string? AdapterId { get; }

    /// <summary>Gets the adapter instance, or null for adapter-free Codecs.</summary>
    IRpcCodecAdapter? Adapter { get; }

    /// <summary>Creates a Codec whose dependencies are resolved from the target Context.</summary>
    /// <param name="provider">The target Context Codec provider.</param>
    /// <param name="adapterScope">The context-owned adapter scope, or <see langword="null"/> for adapter-free Codecs.</param>
    IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope);

    /// <summary>Checks the closed Codec interface without runtime type construction or scanning.</summary>
    bool IsCompatibleCodec(IRpcCodec codec);
}
