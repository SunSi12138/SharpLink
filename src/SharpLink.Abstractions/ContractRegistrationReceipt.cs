namespace SharpLink.Abstractions;

/// <summary>Identifies one registration in a server's registry timeline using scalar facts only.</summary>
/// <param name="ServerInstanceId">The server process identity; generations from other servers are incomparable.</param>
/// <param name="RegistryEpoch">The registry timeline identity within the server process.</param>
/// <param name="Generation">The observed registry generation.</param>
/// <param name="ContractId">The generated wire contract identifier.</param>
/// <param name="RpcAssemblyHash">The exact generated RPC assembly identity.</param>
/// <param name="RegistrationId">The identity of this registration, changed by unregister or replacement.</param>
public sealed record ContractRegistrationReceipt(
    Guid ServerInstanceId,
    Guid RegistryEpoch,
    long Generation,
    long ContractId,
    RpcHash128 RpcAssemblyHash,
    Guid RegistrationId);

/// <summary>Specifies how a contract requirement constrains remote registration identity.</summary>
public enum ContractRegistrationMatch : byte
{
    /// <summary>Requires only matching contract and wire assembly identities.</summary>
    Compatible = 0,

    /// <summary>Also requires the receipt's server and registry timeline at or after its generation.</summary>
    MinimumVersion = 1,

    /// <summary>Also requires the receipt's registration identity in the same timeline at or after its generation.</summary>
    ExactRegistration = 2
}
