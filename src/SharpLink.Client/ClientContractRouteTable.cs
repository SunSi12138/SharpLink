namespace SharpLink.Client;

/// <summary>
/// Owns the control-plane join of topology, handshake identity and remote contracts. Only event
/// publication enumerates manifests; calls read one immutable route by scalar contract identity.
/// Each client owns a separate table, so receipt and hash equality cannot cross cluster scopes.
/// </summary>
internal sealed class ClientContractRouteTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RpcSession, ProtocolV2ContractManifest> _manifests = [];
    private object _topology = new();
    private ClientConnection[][] _connections = [];
    private RoutePublication _publication = RoutePublication.Empty;

    internal void Clear()
    {
        lock (_gate)
        {
            _manifests.Clear();
            _connections = [];
            _topology = new();
            Volatile.Write(ref _publication, RoutePublication.Empty);
        }
    }

    internal void PublishManifest(RpcSession session, ProtocolV2ContractManifest manifest)
    {
        lock (_gate)
        {
            if (_manifests.TryGetValue(session, out var previous) && previous.Generation > manifest.Generation)
                return;
            _manifests[session] = manifest;
            PublishLocked();
        }
    }

    internal void PublishTopology(object topology, ClientConnection[][] connections)
    {
        lock (_gate)
        {
            _topology = topology;
            _connections = connections;
            foreach (var session in _manifests.Keys.Where(static session => !session.IsConnected).ToArray())
                _manifests.Remove(session);
            PublishLocked();
        }
    }

    // A call captures its selector topology together with its contract candidates. Reading the
    // independently published readiness snapshot first can observe a new topology with old routes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetRoute(ClientContractBinding binding, out ClientContractRoute route)
        => TryGetRoute(Volatile.Read(ref _publication), binding, out route);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetRoute(RoutePublication publication, ClientContractBinding binding,
        out ClientContractRoute route)
    {
        if (binding.HasReceipt)
        {
            var receipt = binding.Receipt.GetValueOrDefault();
            return publication.Servers.TryGetValue(new ServerKey(receipt.ServerInstanceId, receipt.RegistryEpoch), out route!);
        }
        return publication.Contracts.TryGetValue(new ContractKey(binding.ContractId, binding.RpcAssemblyHash), out route!);
    }

    private void PublishLocked()
    {
        var contracts = new Dictionary<ContractKey, RouteBuilder>();
        var servers = new Dictionary<ServerKey, RouteBuilder>();
        for (var endpoint = 0; endpoint < _connections.Length; endpoint++)
        {
            foreach (var connection in _connections[endpoint])
            {
                var session = connection.Session;
                if (!connection.CanAcceptCalls)
                    continue;
                if (session.RemoteServerInstanceId != Guid.Empty && session.RemoteRegistryEpoch != Guid.Empty)
                {
                    Add(servers, new ServerKey(session.RemoteServerInstanceId, session.RemoteRegistryEpoch), endpoint, connection);
                }
                if (!_manifests.TryGetValue(session, out var manifest))
                    continue;
                foreach (var contract in manifest.Contracts)
                    Add(contracts, new ContractKey(contract.Key, contract.Value), endpoint, connection);
            }
        }
        Volatile.Write(ref _publication, new RoutePublication(
            _topology,
            contracts.ToFrozenDictionary(static pair => pair.Key, pair => pair.Value.Build(_topology)),
            servers.ToFrozenDictionary(static pair => pair.Key, pair => pair.Value.Build(_topology))));
    }

    private void Add<TKey>(Dictionary<TKey, RouteBuilder> routes, TKey key, int endpoint, ClientConnection connection)
        where TKey : notnull
    {
        if (!routes.TryGetValue(key, out var builder))
            routes.Add(key, builder = new RouteBuilder(_connections.Length));
        builder.Add(endpoint, connection);
    }

    private readonly record struct ContractKey(long ContractId, RpcHash128 RpcAssemblyHash);
    private readonly record struct ServerKey(Guid ServerInstanceId, Guid RegistryEpoch);

    private sealed class RouteBuilder(int endpointCount)
    {
        private readonly List<ClientConnection>?[] _connections = new List<ClientConnection>?[endpointCount];

        internal void Add(int endpoint, ClientConnection connection)
            => (_connections[endpoint] ??= []).Add(connection);

        internal ClientContractRoute Build(object topology)
        {
            var connections = new ClientConnection[_connections.Length][];
            var excluded = 0UL;
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index] = _connections[index]?.ToArray() ?? [];
                if (connections[index].Length == 0)
                    excluded |= 1UL << index;
            }
            return new ClientContractRoute(topology, excluded, connections);
        }
    }

    private sealed record RoutePublication(
        object Topology,
        FrozenDictionary<ContractKey, ClientContractRoute> Contracts,
        FrozenDictionary<ServerKey, ClientContractRoute> Servers)
    {
        internal static readonly RoutePublication Empty = new(new(),
            FrozenDictionary<ContractKey, ClientContractRoute>.Empty,
            FrozenDictionary<ServerKey, ClientContractRoute>.Empty);
    }
}

/// <summary>An immutable eligible connection view, frozen together with its target topology.</summary>
internal sealed class ClientContractRoute(object topology, ulong excludedEndpoints, ClientConnection[][] connections)
{
    internal object Topology { get; } = topology;
    internal ulong ExcludedEndpoints { get; } = excludedEndpoints;

    internal ClientConnection? SelectConnection(int endpoint)
    {
        var candidates = connections[endpoint];
        if (candidates.Length == 0)
            return null;
        if (candidates.Length == 1)
            return candidates[0].TryReserveSelectedCallAdmission() ? candidates[0] : null;
        var first = Random.Shared.Next(candidates.Length);
        var second = Random.Shared.Next(candidates.Length - 1);
        if (second >= first)
            second++;
        var selected = EndpointSelectionKernel.SelectLeastLoaded(candidates, first, second);
        if (selected.TryReserveSelectedCallAdmission())
            return selected;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            if (!ReferenceEquals(candidate, selected) && candidate.TryReserveSelectedCallAdmission())
                return candidate;
        }
        return null;
    }
}
