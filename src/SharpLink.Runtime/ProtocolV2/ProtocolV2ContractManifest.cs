using System.Collections.Frozen;

namespace SharpLink.Runtime;

/// <summary>Immutable callable contract and registration snapshot for one registry timeline.</summary>
internal sealed class ProtocolV2ContractManifest
{
    internal ProtocolV2ContractManifest(long generation, IEnumerable<KeyValuePair<long, RpcHash128>> contracts)
        : this(Guid.Empty, Guid.Empty, generation, contracts, [])
    {
    }

    internal ProtocolV2ContractManifest(
        Guid serverInstanceId,
        Guid registryEpoch,
        long generation,
        IEnumerable<KeyValuePair<long, RpcHash128>> contracts,
        IEnumerable<KeyValuePair<long, Guid>> registrationIds)
    {
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if ((serverInstanceId == Guid.Empty) != (registryEpoch == Guid.Empty))
            throw new ArgumentException("Server and registry identities must both be present or both absent.");
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(registrationIds);
        var ordered = contracts.OrderBy(static pair => pair.Key).ToArray();
        var dictionary = new Dictionary<long, RpcHash128>(ordered.Length);
        foreach (var pair in ordered)
        {
            if (pair.Key == 0)
                throw new ArgumentException("Contract manifest entries must use a non-zero contract ID.", nameof(contracts));
            if (pair.Value.IsEmpty)
                throw new ArgumentException("Contract manifest entries must use a non-empty RpcAssemblyHash.", nameof(contracts));
            if (!dictionary.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Contract manifest contains duplicate contract ID {pair.Key}.", nameof(contracts));
        }
        var registrations = new Dictionary<long, Guid>();
        foreach (var pair in registrationIds)
        {
            if (!dictionary.ContainsKey(pair.Key) || !registrations.TryAdd(pair.Key, pair.Value) ||
                serverInstanceId != Guid.Empty && pair.Value == Guid.Empty)
                throw new ArgumentException("Registration identities must uniquely match callable contracts.", nameof(registrationIds));
        }
        if (serverInstanceId != Guid.Empty && registrations.Count != dictionary.Count)
            throw new ArgumentException("Every callable contract requires a registration identity.", nameof(registrationIds));
        ServerInstanceId = serverInstanceId;
        RegistryEpoch = registryEpoch;
        Generation = generation;
        Contracts = dictionary.ToFrozenDictionary();
        OrderedContracts = ordered;
        RegistrationIds = registrations.ToFrozenDictionary();
    }

    internal Guid ServerInstanceId { get; }
    internal Guid RegistryEpoch { get; }
    internal long Generation { get; }
    internal FrozenDictionary<long, RpcHash128> Contracts { get; }
    internal IReadOnlyList<KeyValuePair<long, RpcHash128>> OrderedContracts { get; }
    internal FrozenDictionary<long, Guid> RegistrationIds { get; }
}
