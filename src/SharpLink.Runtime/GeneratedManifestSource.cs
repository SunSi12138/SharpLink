namespace SharpLink.Runtime;

/// <summary>
/// Provides a point-in-time generated-manifest snapshot for one cold-path Compile operation.
/// Implementations are discovery inputs only; Runtime instances never query a source after Compile.
/// </summary>
internal interface IGeneratedManifestSource
{
    IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot();
}

/// <summary>
/// Adapts the weak process bootstrap catalog without caching or taking ownership of its entries.
/// </summary>
internal sealed class GlobalCatalogManifestSource : IGeneratedManifestSource
{
    private GlobalCatalogManifestSource()
    {
    }

    internal static GlobalCatalogManifestSource Instance { get; } = new();

    public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
        => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
}

/// <summary>
/// Provides an immutable caller-supplied snapshot for isolated builds and multi-cluster children.
/// The source owns no manifest lifetime beyond the explicit snapshot reference supplied by its caller.
/// </summary>
internal sealed class FixedGeneratedManifestSource : IGeneratedManifestSource
{
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _manifests;

    internal FixedGeneratedManifestSource(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        _manifests = manifests; // External input until one bounded Capture; no eager framework copy.
    }

    internal static FixedGeneratedManifestSource Empty { get; } = new([]);

    public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot() => _manifests;
}

/// <summary>
/// One immutable, strong snapshot captured from an <see cref="IGeneratedManifestSource"/>. Its
/// lifetime belongs to the BuildPlan and materialized Runtime, never to a process-global source.
/// </summary>
internal sealed class GeneratedManifestSnapshot
{
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _manifests;
    private GeneratedManifestSnapshot(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
        => _manifests = manifests;
    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> Manifests => _manifests;

    internal static GeneratedManifestSnapshot Capture(IGeneratedManifestSource source, ColdMetadataPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preparation);
        using var callback = preparation.EnterCallback();
        var manifests = ReferenceEquals(source, GlobalCatalogManifestSource.Instance)
            ? ColdMetadataCatalog.CaptureManifests(preparation)
            : source.CreateSnapshot() ?? throw ColdMetadataException.Invalid("Manifest source returned a null snapshot.");
        var captured = ColdMetadataCollections.Copy(manifests, preparation, transform:
            manifest => PreparedGeneratedManifest.Capture(manifest, preparation));
        return new GeneratedManifestSnapshot(captured);
    }

    internal void ValidateForPlanCompilation()
    {
        // Capture returns only fully validated immutable manifests; there is no raw-list path.
        foreach (var manifest in _manifests)
            if (manifest is not PreparedGeneratedManifest)
                throw new InvalidOperationException("A Runtime plan requires prepared metadata.");
    }
}
