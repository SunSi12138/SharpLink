namespace SharpLink.Abstractions;

/// <summary>Specifies the scalar wire identity and optional registration constraint for a contract.</summary>
/// <remarks>Requirements retain no contract type, assembly, proxy, or runtime registration.</remarks>
public sealed record ContractRequirement
{
    /// <summary>Creates a requirement for exact wire compatibility, independent of registration identity.</summary>
    /// <param name="contractId">The nonzero generated contract identifier.</param>
    /// <param name="rpcAssemblyHash">The nonempty generated RPC assembly identity.</param>
    public ContractRequirement(long contractId, RpcHash128 rpcAssemblyHash)
    {
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId), "A nonzero wire contract ID is required.");
        if (rpcAssemblyHash.IsEmpty)
            throw new ArgumentException("A nonempty RPC assembly hash is required.", nameof(rpcAssemblyHash));
        ContractId = contractId;
        RpcAssemblyHash = rpcAssemblyHash;
    }

    /// <summary>Creates a requirement scoped to an explicitly observed server registry timeline.</summary>
    /// <remarks>Compatible matching uses only the receipt's contract and hash; it does not retain a registration constraint.</remarks>
    /// <param name="registration">The scalar receipt identifying the contract registration.</param>
    /// <param name="match">The registration constraint to enforce in addition to wire compatibility.</param>
    public ContractRequirement(ContractRegistrationReceipt registration, ContractRegistrationMatch match)
        : this(GetContractId(registration), registration.RpcAssemblyHash)
    {
        if (!Enum.IsDefined(match))
            throw new ArgumentOutOfRangeException(nameof(match));
        if (match == ContractRegistrationMatch.Compatible)
            return;
        if (registration.ServerInstanceId == Guid.Empty || registration.RegistryEpoch == Guid.Empty ||
            registration.Generation < 0 ||
            match == ContractRegistrationMatch.ExactRegistration && registration.RegistrationId == Guid.Empty)
            throw new ArgumentException("A receipt must identify a valid server registry timeline and registration.", nameof(registration));
        Registration = registration;
        Match = match;
    }

    /// <summary>Gets the generated wire contract identifier.</summary>
    public long ContractId { get; }

    /// <summary>Gets the exact generated RPC assembly identity.</summary>
    public RpcHash128 RpcAssemblyHash { get; }

    /// <summary>Gets the optional scalar registration receipt.</summary>
    public ContractRegistrationReceipt? Registration { get; }

    /// <summary>Gets the registration constraint, independently of wire compatibility.</summary>
    public ContractRegistrationMatch Match { get; }

    internal RpcAvailability Availability { get; init; }

    internal Task? LocalRetirement { get; init; }

    internal bool Matches(Guid serverInstanceId, Guid registryEpoch, long generation, Guid registrationId)
        => Match == ContractRegistrationMatch.Compatible ||
           Registration is { } receipt &&
           receipt.ServerInstanceId == serverInstanceId && receipt.RegistryEpoch == registryEpoch &&
           generation >= receipt.Generation &&
           (Match != ContractRegistrationMatch.ExactRegistration || receipt.RegistrationId == registrationId);

    private static long GetContractId(ContractRegistrationReceipt registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration.ContractId;
    }
}
