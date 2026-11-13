#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

// The entire immutable constraint participates in cold site and coalescing keys.
internal readonly record struct StaticBindingScope(
    RpcRegistrationRequirement Requirement, RpcRegistrationReceipt? Receipt)
{
    internal bool IsValid => Requirement is >= RpcRegistrationRequirement.Compatible and <= RpcRegistrationRequirement.ExactRegistration &&
        (Receipt is { } receipt
            ? receipt.ServerInstanceId != Guid.Empty && receipt.RegistryEpoch != Guid.Empty && receipt.Generation >= 0 &&
              receipt.ContractId != 0 && !receipt.RpcAssemblyHash.IsEmpty && receipt.RegistrationId != 0
            : Requirement == RpcRegistrationRequirement.Compatible);

    internal bool MatchesPeer(Guid server, Guid epoch)
        => Receipt is not { } receipt || receipt.ServerInstanceId == server && receipt.RegistryEpoch == epoch;

    internal bool MatchesRegistration(long contract, RpcHash128 hash, long registration, long generation)
        => Receipt is not { } receipt || receipt.ContractId == contract && receipt.RpcAssemblyHash == hash &&
           (Requirement == RpcRegistrationRequirement.Compatible || generation >= receipt.Generation) &&
           (Requirement != RpcRegistrationRequirement.ExactRegistration || registration == receipt.RegistrationId);
}
#endif
