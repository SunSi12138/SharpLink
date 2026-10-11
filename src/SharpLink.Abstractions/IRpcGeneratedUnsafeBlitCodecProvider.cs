namespace SharpLink.Abstractions;

/// <summary>Identifies a generated raw Codec factory and its finalized ABI requirement.</summary>
/// <remarks>The owning manifest registration snapshots this metadata before Codec resolution.</remarks>
public interface IRpcGeneratedUnsafeBlitCodecFactory : IRpcGeneratedCodecFactory
{
    /// <summary>Gets the finalized ABI requirement carried by this factory.</summary>
    SharpLinkGeneratedUnsafeBlitRequirement Requirement { get; }
}

/// <summary>Constructs a raw Codec only for an admitted owner-local generated factory.</summary>
/// <remarks>This capability validates registered factory identity and never resolves missing metadata through fallback.</remarks>
public interface IRpcGeneratedUnsafeBlitCodecProvider
{
    /// <summary>Validates the registered factory, target Type and snapshotted ABI before returning its raw Codec.</summary>
    IRpcCodec<T> GetGeneratedUnsafeBlitCodec<T>(
        IRpcGeneratedUnsafeBlitCodecFactory factory,
        SharpLinkGeneratedUnsafeBlitRequirement requirement);
}
