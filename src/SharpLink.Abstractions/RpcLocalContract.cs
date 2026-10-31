namespace SharpLink.Abstractions;

/// <summary>An immutable client-issued selector for one exact local contract registration.</summary>
/// <remarks>
/// This is neither a remote binding token nor a lifetime lease. It contains only host scalars,
/// never an assembly, type, codec, stub, or collectible module. Re-registration does not update it.
/// </remarks>
public sealed class RpcLocalContract
{
    internal RpcLocalContract(
        Guid issuerId,
        long registrationGeneration,
        long contractId,
        RpcHash128 rpcAssemblyHash)
    {
        if (issuerId == Guid.Empty)
            throw new ArgumentException("A nonempty local issuer identity is required.", nameof(issuerId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(registrationGeneration, 0);
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId), "A nonzero contract identifier is required.");
        if (rpcAssemblyHash.IsEmpty)
            throw new ArgumentException("A nonempty RPC assembly hash is required.", nameof(rpcAssemblyHash));
        IssuerId = issuerId;
        RegistrationGeneration = registrationGeneration;
        ContractId = contractId;
        RpcAssemblyHash = rpcAssemblyHash;
    }

    /// <summary>Gets the identity of the client that issued this selector.</summary>
    public Guid IssuerId { get; }

    /// <summary>Gets the exact local registration generation, not a remote registry generation.</summary>
    public long RegistrationGeneration { get; }

    /// <summary>Gets the generated contract identifier.</summary>
    public long ContractId { get; }

    /// <summary>Gets the complete generated RPC assembly identity.</summary>
    public RpcHash128 RpcAssemblyHash { get; }
}
