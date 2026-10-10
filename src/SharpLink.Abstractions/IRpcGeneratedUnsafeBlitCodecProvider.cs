namespace SharpLink.Abstractions;

/// <summary>Constructs a raw codec using an ABI requirement carried by its generated factory.</summary>
/// <remarks>This capability never resolves missing requirements through a dynamic or reflective fallback.</remarks>
public interface IRpcGeneratedUnsafeBlitCodecProvider
{
    /// <summary>Validates the supplied generated ABI and returns its raw codec.</summary>
    IRpcCodec<T> GetGeneratedUnsafeBlitCodec<T>(SharpLinkGeneratedUnsafeBlitRequirement requirement);
}
