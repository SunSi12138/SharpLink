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
        var snapshot = new ISharpLinkGeneratedAssemblyManifest[manifests.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = manifests[index] ?? throw new ArgumentException(
                "Generated manifest snapshots cannot contain null.",
                nameof(manifests));
        }
        _manifests = Array.AsReadOnly(snapshot);
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
    {
        var snapshot = new ISharpLinkGeneratedAssemblyManifest[manifests.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = manifests[index] ?? throw new ArgumentException(
                "Generated manifest snapshots cannot contain null.",
                nameof(manifests));
        }
        _manifests = Array.AsReadOnly(snapshot);
    }

    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> Manifests => _manifests;

    internal static GeneratedManifestSnapshot Empty { get; } = new([]);

    /// <summary>Calls the source exactly once, then immediately severs the plan from it.</summary>
    internal static GeneratedManifestSnapshot Capture(IGeneratedManifestSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var manifests = source.CreateSnapshot() ?? throw new InvalidOperationException(
            "A generated ManifestSource returned a null snapshot.");
        return new GeneratedManifestSnapshot(manifests);
    }

    internal static GeneratedManifestSnapshot FromManifests(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
        => Capture(new FixedGeneratedManifestSource(manifests));

    /// <summary>
    /// Performs pure API/Protocol, descriptor-shape, and ownership validation without creating
    /// Codec or adapter resources.
    /// </summary>
    internal void ValidateForPlanCompilation()
    {
        for (var index = 0; index < _manifests.Count; index++)
            SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(_manifests[index]);
    }
}
