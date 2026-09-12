namespace SharpLink.Abstractions;

/// <summary>
/// Binds a compile-time referenced generated Codec to the exact runtime target type and semantic hash
/// that the consuming generated assembly was compiled against.
/// </summary>
public sealed record SharpLinkReferencedCodecDependency(
    Type TargetType,
    RpcHash128 ExpectedCodecHash);

/// <summary>
/// Optional generated-manifest capability that publishes binding-aware referenced Codec dependencies.
/// The target <see cref="Type"/> preserves the exact assembly/load-context generation selected by the
/// consumer, while <see cref="SharpLinkReferencedCodecDependency.ExpectedCodecHash"/> locks the
/// expected generated Codec semantics.
/// </summary>
public interface ISharpLinkReferencedCodecDependencyManifest
{
    /// <summary>Gets the referenced generated Codec dependencies required by this manifest.</summary>
    IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; }
}
