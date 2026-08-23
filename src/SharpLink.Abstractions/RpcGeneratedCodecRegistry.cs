namespace SharpLink.Abstractions;

/// <summary>Creates one source-generated Codec for an immutable runtime-context snapshot.</summary>
public interface IRpcGeneratedCodecFactory
{
    /// <summary>Gets the closed DTO or collection type handled by the factory.</summary>
    Type TargetType { get; }

    /// <summary>Gets the deterministic schema identifier used for idempotent registration.</summary>
    string SchemaId { get; }

    /// <summary>Gets the stable binary wire-format identity.</summary>
    string WireFormatId { get; }

    /// <summary>Gets the adapter lifecycle identity, or null for native Codecs.</summary>
    string? AdapterId { get; }

    /// <summary>Gets the adapter instance, or null for native Codecs.</summary>
    IRpcCodecAdapter? Adapter { get; }

    /// <summary>
    /// Gets whether this factory is selected by an assembly-level route and must only be
    /// resolved through artifacts owned by the same generated manifest.
    /// </summary>
    bool IsManifestScoped => false;

    /// <summary>Creates a Codec whose dependencies are resolved from the target Context.</summary>
    /// <param name="provider">The target Context Codec provider.</param>
    /// <param name="adapterScope">The context-owned adapter scope, or <see langword="null"/> for native codecs.</param>
    IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope);

    /// <summary>Checks the closed Codec interface without runtime type construction or scanning.</summary>
    bool IsCompatibleCodec(IRpcCodec codec);
}