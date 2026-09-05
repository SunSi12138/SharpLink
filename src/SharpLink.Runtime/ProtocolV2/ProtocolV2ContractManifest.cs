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
        IEnumerable<KeyValuePair<long, RpcHash128>> contracts)
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
    }

    internal long Generation { get; }

    internal FrozenDictionary<long, RpcHash128> Contracts { get; }

    internal IReadOnlyList<KeyValuePair<long, RpcHash128>> OrderedContracts { get; }
}
