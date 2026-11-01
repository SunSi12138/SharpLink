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
        _manifests = manifests;
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

    private GeneratedClusterRouteSnapshot(IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> routes)
        => _routes = routes;

    internal IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes => _routes;

    internal static GeneratedClusterRouteSnapshot Empty { get; } = new([]);

    internal static GeneratedClusterRouteSnapshot Capture(
        IGeneratedClusterRouteSource source, ColdMetadataPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preparation);
        // Reserve the result header before invoking even the source's first callback.
        var workingCharge = preparation.ReserveCollection(0);
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> manifests;
        using (preparation.EnterCallback())
            manifests = ReferenceEquals(source, GlobalCatalogClusterRouteSource.Instance)
                ? ColdMetadataCatalog.CaptureRoutes(preparation)
                : source.CreateSnapshot() ?? throw new InvalidOperationException(
                    "A generated cluster RouteSource returned a null snapshot.");
        var routes = new List<SharpLinkGeneratedClusterAssemblyRoute>();
        using (preparation.EnterCallback())
        {
            var manifestCount = manifests.Count;
            for (var manifestIndex = 0; manifestIndex < manifestCount; manifestIndex++)
            {
                var manifest = manifests[manifestIndex] ?? throw new InvalidOperationException(
                    "Generated cluster route snapshots cannot contain null.");
                var manifestRoutes = manifest.Routes ?? throw new InvalidOperationException(
                    "A generated cluster route manifest returned a null route list.");
                var routeCount = manifestRoutes.Count;
                for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
                {
                    var route = manifestRoutes[routeIndex] ?? throw new InvalidOperationException(
                        "A generated cluster route manifest returned a null route.");
                    if (!SharpLinkClusterKey.IsValid(route.Cluster.Value))
                        throw new InvalidOperationException("A generated cluster route contains an invalid cluster key.");
                    ArgumentNullException.ThrowIfNull(route.ContractAssembly);
                    ArgumentException.ThrowIfNullOrWhiteSpace(route.ContractAssemblyIdentity);
                    var capacity = checked(routes.Count + 1);
                    workingCharge.GrowTo(capacity);
                    routes.Capacity = capacity;
                    routes.Add(route);
                }
            }
        }
        preparation.ReserveCollection(routes.Count);
        return new GeneratedClusterRouteSnapshot(Array.AsReadOnly(routes.ToArray()));
    }
}
