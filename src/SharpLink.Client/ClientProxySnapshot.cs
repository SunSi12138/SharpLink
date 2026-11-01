using System.Reflection;

namespace SharpLink.Client;

// This reference count belongs exclusively to cold registry preparation readers. No RPC
// selection/send path acquires it. The final cold owner clears the map before its charge.
internal sealed class ClientProxySnapshot : IDisposable
{
    private FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration>? _map;
    private IDisposable? _metadata;
    private int _references = 1;
    private Source[] _sources = [];
    internal ClientProxySnapshot(FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> map,
        IDisposable? metadata = null)
    { _map = map; _metadata = metadata; }
    internal static ClientProxySnapshot Create(
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> map,
        ColdMetadataPreparation preparation,
        ClientProxySnapshot? current = null,
        PreparedGeneratedManifest? incoming = null,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null)
    {
        preparation.ReserveCollection(map.Count);
        var needed = new HashSet<Assembly>(map.Count, ReferenceEqualityComparer.Instance);
        foreach (var registration in map.Values) needed.Add(registration.Descriptor.ContractType.Assembly);
        var maximumSources = checked((current?._sources.Length ?? 0) +
            (staticManifests?.Count ?? 0) + (incoming is null ? 0 : 1));
        preparation.ReserveCollection(maximumSources);
        var sources = new List<Source>(maximumSources);
        void Add(Assembly assembly, ColdMetadataPreparation source)
        {
            if (!needed.Remove(assembly)) return;
            preparation.Share(source);
            sources.Add(new Source(assembly, source));
        }
        if (incoming is not null) Add(incoming.OwnerAssembly, incoming.Preparation);
        if (current is not null)
            foreach (var source in current._sources) Add(source.Assembly, source.Preparation);
        if (staticManifests is not null)
            foreach (var manifest in staticManifests)
            {
                if (manifest is not PreparedGeneratedManifest prepared)
                    throw new InvalidOperationException("Proxy snapshots require prepared manifest ownership.");
                Add(prepared.OwnerAssembly, prepared.Preparation);
            }
        if (needed.Count != 0)
            throw new InvalidOperationException("A copied proxy snapshot has lost an exact source metadata owner.");
        preparation.ReserveCollection(sources.Count);
        return new ClientProxySnapshot(map, preparation.Retain()) { _sources = sources.ToArray() };
    }
    private readonly record struct Source(Assembly Assembly, ColdMetadataPreparation Preparation);

    internal FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> Map =>
        _map ?? throw new ObjectDisposedException(nameof(ClientProxySnapshot));
    internal ClientProxySnapshot Retain()
    {
        // Caller holds the registry gate, so the published owner cannot concurrently disappear.
        if (Interlocked.Increment(ref _references) <= 1)
            throw new ObjectDisposedException(nameof(ClientProxySnapshot));
        return this;
    }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        _map = null;
        _sources = [];
        Interlocked.Exchange(ref _metadata, null)?.Dispose();
    }
}
