using System.Reflection;

namespace SharpLink.Client;

// Cold/public-control readers explicitly retain this immutable snapshot. These RMWs are
// not used by ordinary RPC dispatch: generated handles already own a captured child site.
internal sealed class MultiClusterSnapshot : IDisposable
{
    private SharedMap<SharpLinkClusterKey, SharpLinkClusterSlot>? _clusters;
    private SharedMap<Type, SharpLinkClusterRouteRegistration>? _routes;
    private SharpLinkClusterSlot[] _slots;
    private Source[] _sources;
    private IDisposable? _metadata;
    private int _references = 1;
    private readonly bool _immortal;

    private MultiClusterSnapshot(SharedMap<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        SharedMap<Type, SharpLinkClusterRouteRegistration> routes, int budget,
        SharpLinkClusterSlot[] slots, Source[] sources, IDisposable? metadata, bool immortal = false)
    {
        _clusters = clusters; _routes = routes; ConfiguredConnectionBudget = budget;
        _slots = slots; _sources = sources; _metadata = metadata; _immortal = immortal;
    }
    internal FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> Clusters => _clusters!.Map;
    internal FrozenDictionary<Type, SharpLinkClusterRouteRegistration> Routes => _routes!.Map;
    internal SharpLinkClusterSlot[] Slots => _slots;
    internal int ConfiguredConnectionBudget { get; }

    internal static MultiClusterSnapshot Create(
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> routes, int budget,
        ColdMetadataPreparation preparation, MultiClusterSnapshot? previous = null,
        PreparedGeneratedManifest? incoming = null)
    {
        // The caller reserves each newly built map before allocation. Shared maps keep
        // their exact previous map owner, not a chain of previous whole snapshots.
        preparation.ReserveCollection(clusters.Count);
        var slots = clusters.Values.ToArray();
        preparation.ReserveCollection(routes.Count);
        var needed = new HashSet<Assembly>(routes.Count, ReferenceEqualityComparer.Instance);
        foreach (var route in routes.Values) needed.Add(route.OwnerAssembly);
        var maximum = checked((previous?._sources.Length ?? 0) + (incoming is null ? 0 : 1));
        foreach (var slot in slots) maximum = checked(maximum + (slot.StaticManifests?.Count ?? 0));
        preparation.ReserveCollection(maximum);
        var sources = new List<Source>(maximum);
        void Add(Assembly assembly, ColdMetadataPreparation owner)
        {
            if (!needed.Remove(assembly)) return;
            preparation.Share(owner);
            sources.Add(new Source(assembly, owner));
        }
        if (incoming is not null) Add(incoming.OwnerAssembly, incoming.Preparation);
        if (previous is not null)
            foreach (var source in previous._sources) Add(source.Assembly, source.Preparation);
        foreach (var slot in slots)
            if (slot.StaticManifests is { } manifests)
                foreach (var manifest in manifests)
                    if (manifest is PreparedGeneratedManifest prepared)
                        Add(prepared.OwnerAssembly, prepared.Preparation);
        if (needed.Count != 0)
            throw new InvalidOperationException("A coordinator snapshot requires exact prepared source ownership for every route.");
        preparation.ReserveCollection(sources.Count);
        var sourceArray = sources.ToArray();
        var clusterOwner = previous is not null && ReferenceEquals(clusters, previous.Clusters)
            ? previous._clusters!.Retain() : new SharedMap<SharpLinkClusterKey, SharpLinkClusterSlot>(clusters, preparation.Retain());
        var routeOwner = previous is not null && ReferenceEquals(routes, previous.Routes)
            ? previous._routes!.Retain() : new SharedMap<Type, SharpLinkClusterRouteRegistration>(routes, preparation.Retain());
        return new MultiClusterSnapshot(clusterOwner, routeOwner, budget, slots, sourceArray, preparation.Retain());
    }

    internal MultiClusterSnapshot Retain()
    {
        if (!_immortal && Interlocked.Increment(ref _references) <= 1)
            throw new ObjectDisposedException(nameof(MultiClusterSnapshot));
        return this; // Acquired under coordinator gate, before publication can release it.
    }
    public void Dispose()
    {
        if (_immortal || Interlocked.Decrement(ref _references) != 0) return;
        _slots = []; _sources = [];
        Interlocked.Exchange(ref _clusters, null)?.Dispose();
        Interlocked.Exchange(ref _routes, null)?.Dispose();
        Interlocked.Exchange(ref _metadata, null)?.Dispose();
    }
    private readonly record struct Source(Assembly Assembly, ColdMetadataPreparation Preparation);
    private sealed class SharedMap<TKey, TValue>(FrozenDictionary<TKey, TValue> map, IDisposable? metadata)
        : IDisposable where TKey : notnull
    {
        private FrozenDictionary<TKey, TValue>? _map = map;
        private IDisposable? _metadata = metadata;
        private int _references = 1;
        internal FrozenDictionary<TKey, TValue> Map => _map!;
        internal SharedMap<TKey, TValue> Retain() { Interlocked.Increment(ref _references); return this; }
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            _map = null;
            Interlocked.Exchange(ref _metadata, null)?.Dispose();
        }
    }
    internal static MultiClusterSnapshot Empty { get; } = new(
        new(FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot>.Empty, null),
        new(FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty, null), 0, [], [], null, true);
}
