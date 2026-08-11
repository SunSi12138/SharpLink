namespace SharpLink.Client;

/// <summary>
/// Provides generated multi-cluster route manifests for one cold-path coordinator Compile.
/// Dynamic cluster and assembly mutation remain explicit instance operations and never mutate this source.
/// </summary>
internal interface IGeneratedClusterRouteSource
{
    IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot();
}

/// <summary>Adapts the weak process route catalog without caching its entries.</summary>
internal sealed class GlobalCatalogClusterRouteSource : IGeneratedClusterRouteSource
{
    private GlobalCatalogClusterRouteSource()
    {
    }

    internal static GlobalCatalogClusterRouteSource Instance { get; } = new();

    public IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot()
        => SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot();
}

/// <summary>Provides an immutable route-manifest list for isolated coordinator builds.</summary>
internal sealed class FixedGeneratedClusterRouteSource : IGeneratedClusterRouteSource
{
    private readonly IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> _manifests;

    internal FixedGeneratedClusterRouteSource(
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var snapshot = new ISharpLinkGeneratedClusterRouteManifest[manifests.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = manifests[index] ?? throw new ArgumentException(
                "Generated cluster route snapshots cannot contain null.",
                nameof(manifests));
        }
        _manifests = Array.AsReadOnly(snapshot);
    }

    internal static FixedGeneratedClusterRouteSource Empty { get; } = new([]);

    public IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot() => _manifests;
}

/// <summary>
/// Flattened immutable route records. It deliberately does not retain route-manifest provider objects.
/// </summary>
internal sealed class GeneratedClusterRouteSnapshot
{
    private readonly IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> _routes;

    private GeneratedClusterRouteSnapshot(
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> manifests)
    {
        var routes = new List<SharpLinkGeneratedClusterAssemblyRoute>();
        for (var manifestIndex = 0; manifestIndex < manifests.Count; manifestIndex++)
        {
            var manifest = manifests[manifestIndex] ?? throw new ArgumentException(
                "Generated cluster route snapshots cannot contain null.",
                nameof(manifests));
            var manifestRoutes = manifest.Routes ?? throw new InvalidOperationException(
                "A generated cluster route manifest returned a null route list.");
            for (var routeIndex = 0; routeIndex < manifestRoutes.Count; routeIndex++)
            {
                var route = manifestRoutes[routeIndex] ?? throw new InvalidOperationException(
                    "A generated cluster route manifest returned a null route.");
                if (!SharpLinkClusterKey.IsValid(route.Cluster.Value))
                    throw new InvalidOperationException("A generated cluster route contains an invalid cluster key.");
                ArgumentNullException.ThrowIfNull(route.ContractAssembly);
                ArgumentException.ThrowIfNullOrWhiteSpace(route.ContractAssemblyIdentity);
                routes.Add(route);
            }
        }
        _routes = Array.AsReadOnly(routes.ToArray());
    }

    internal IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes => _routes;

    internal static GeneratedClusterRouteSnapshot Empty { get; } = new([]);

    internal static GeneratedClusterRouteSnapshot Capture(IGeneratedClusterRouteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var manifests = source.CreateSnapshot() ?? throw new InvalidOperationException(
            "A generated cluster RouteSource returned a null snapshot.");
        return new GeneratedClusterRouteSnapshot(manifests);
    }

    internal static GeneratedClusterRouteSnapshot FromManifests(
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> manifests)
        => Capture(new FixedGeneratedClusterRouteSource(manifests));
}
