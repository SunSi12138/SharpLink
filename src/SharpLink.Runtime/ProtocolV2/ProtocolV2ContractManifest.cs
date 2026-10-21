using System.Collections.Frozen;

namespace SharpLink.Runtime;

/// <summary>
/// Immutable connection-control snapshot mapping each remotely callable contract to the
/// deterministic wire identity of its owning contract assembly.
/// </summary>
internal sealed class ProtocolV2ContractManifest
{
    internal ProtocolV2ContractManifest(
        long generation,
        IEnumerable<KeyValuePair<long, RpcHash128>> contracts,
        Guid routeScope = default,
        IEnumerable<KeyValuePair<long, long>>? routes = null)
    {
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(contracts);

        var ordered = contracts.OrderBy(static pair => pair.Key).ToArray();
        var dictionary = new Dictionary<long, RpcHash128>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var pair = ordered[index];
            if (pair.Key == 0)
                throw new ArgumentException("Contract manifest entries must use a non-zero contract ID.", nameof(contracts));
            if (pair.Value.IsEmpty)
                throw new ArgumentException("Contract manifest entries must use a non-empty RpcAssemblyHash.", nameof(contracts));
            if (!dictionary.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Contract manifest contains duplicate contract ID {pair.Key}.", nameof(contracts));
        }

        Generation = generation;
        Contracts = dictionary.ToFrozenDictionary();
        OrderedContracts = ordered;
        RouteScope = routeScope;
        if (routeScope == Guid.Empty)
        {
            if (routes is not null)
                throw new ArgumentException("Routes require a nonempty scope.", nameof(routes));
            Routes = FrozenDictionary<long, long>.Empty;
        }
        else
        {
            ArgumentNullException.ThrowIfNull(routes);
            var routeMap = routes.ToDictionary();
            if (routeMap.Count != dictionary.Count ||
                routeMap.Any(pair => !dictionary.ContainsKey(pair.Key) || pair.Value <= 0) ||
                routeMap.Values.Distinct().Count() != routeMap.Count)
                throw new ArgumentException("Prepared routes must identify every contract exactly once with unique positive IDs.", nameof(routes));
            Routes = routeMap.ToFrozenDictionary();
        }
    }

    internal long Generation { get; }

    internal FrozenDictionary<long, RpcHash128> Contracts { get; }

    internal IReadOnlyList<KeyValuePair<long, RpcHash128>> OrderedContracts { get; }

    // Experimental cold-control extension, not negotiated production protocol support.
    internal Guid RouteScope { get; }
    internal FrozenDictionary<long, long> Routes { get; }
}
