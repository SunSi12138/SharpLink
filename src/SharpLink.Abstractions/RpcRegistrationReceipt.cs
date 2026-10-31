namespace SharpLink.Abstractions;

/// <summary>Specifies the registration predicate required of an exact contract binding.</summary>
public enum RpcBindingRequirement : byte
{
    /// <summary>Requires the exact contract and assembly hash within the captured scope.</summary>
    Compatible = 0,

    /// <summary>Also requires the selected registration's own publication generation to meet the receipt.</summary>
    MinimumVersion = 1,

    /// <summary>Also requires the exact registration identifier in the receipt.</summary>
    ExactRegistration = 2
}

/// <summary>A scalar observation of one remote registration, not authorization or a call lease.</summary>
public readonly record struct RpcRegistrationReceipt
{
    /// <summary>Creates a receipt for an exact server, registry epoch, and contract registration.</summary>
    /// <param name="serverInstanceId">The nonempty server-lifetime authority identity.</param>
    /// <param name="registryEpoch">The nonempty registry-lifetime authority identity.</param>
    /// <param name="generation">The registration's own positive publication generation.</param>
    /// <param name="registrationId">The positive exact registration identifier.</param>
    /// <param name="contractId">The nonzero generated contract identifier.</param>
    /// <param name="rpcAssemblyHash">The complete nonempty RPC assembly identity.</param>
    public RpcRegistrationReceipt(
        Guid serverInstanceId,
        Guid registryEpoch,
        long generation,
        long registrationId,
        long contractId,
        RpcHash128 rpcAssemblyHash)
    {
        if (serverInstanceId == Guid.Empty)
            throw new ArgumentException("A nonempty server identity is required.", nameof(serverInstanceId));
        if (registryEpoch == Guid.Empty)
            throw new ArgumentException("A nonempty registry epoch is required.", nameof(registryEpoch));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(generation, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(registrationId, 0);
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId), "A nonzero contract identifier is required.");
        if (rpcAssemblyHash.IsEmpty)
            throw new ArgumentException("A nonempty RPC assembly hash is required.", nameof(rpcAssemblyHash));
        ServerInstanceId = serverInstanceId;
        RegistryEpoch = registryEpoch;
        Generation = generation;
        RegistrationId = registrationId;
        ContractId = contractId;
        RpcAssemblyHash = rpcAssemblyHash;
    }

    /// <summary>Gets the server-lifetime authority identity.</summary>
    public Guid ServerInstanceId { get; }
    /// <summary>Gets the registry-lifetime authority identity.</summary>
    public Guid RegistryEpoch { get; }
    /// <summary>Gets this registration's own publication generation.</summary>
    public long Generation { get; }
    /// <summary>Gets the exact registration identifier.</summary>
    public long RegistrationId { get; }
    /// <summary>Gets the generated contract identifier.</summary>
    public long ContractId { get; }
    /// <summary>Gets the complete RPC assembly identity.</summary>
    public RpcHash128 RpcAssemblyHash { get; }

    internal bool IsValid => ServerInstanceId != Guid.Empty && RegistryEpoch != Guid.Empty &&
        Generation > 0 && RegistrationId > 0 && ContractId != 0 && !RpcAssemblyHash.IsEmpty;
}
