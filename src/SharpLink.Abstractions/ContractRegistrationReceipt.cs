namespace SharpLink.Abstractions;

/// <summary>Identifies one published contract registration on one server registry timeline.</summary>
/// <remarks>
/// A receipt can constrain a later invocation across independent connections to the same server.
/// It is neither authorization nor delivery acknowledgement. Versions cannot be compared across
/// server instances or registry epochs, and later replacement can make an exact receipt stale.
/// </remarks>
/// <param name="ServerInstanceId">The server instance that published the registration.</param>
/// <param name="RegistryEpoch">The registry timeline within that instance.</param>
/// <param name="Generation">The monotonically increasing publication version within that timeline.</param>
/// <param name="ContractId">The generated contract identifier.</param>
/// <param name="RpcAssemblyHash">The generated wire identity of the contract assembly.</param>
/// <param name="RegistrationId">The identity of this registration, including same-hash replacements.</param>
public readonly record struct ContractRegistrationReceipt(
    Guid ServerInstanceId,
    Guid RegistryEpoch,
    ulong Generation,
    long ContractId,
    RpcHash128 RpcAssemblyHash,
    Guid RegistrationId);

/// <summary>Constrains server admission of a request carrying a registration receipt.</summary>
public enum RpcRegistrationRequirement : byte
{
    /// <summary>Requires the requested contract and assembly hash on the receipt's server and epoch.</summary>
    Compatible,
    /// <summary>Also requires a registration generation at least as recent as the receipt.</summary>
    MinimumVersion,
    /// <summary>Also requires the exact registration identified by the receipt.</summary>
    ExactRegistration
}

/// <summary>Immutable local options captured when a generated RPC proxy is created.</summary>
/// <remarks>
/// Proxy creation never waits for a connection or a remote manifest. Each invocation uses the
/// current eligible route and fails with Unavailable/NoRoutableTarget when none exists. An explicit
/// receipt may route through a stale manifest only after the connection's server and registry
/// identities match; the server still validates the request before business decoding.
/// </remarks>
public sealed record RpcProxyOptions
{
    /// <summary>Gets the optional registration receipt constraining every invocation of this proxy.</summary>
    public ContractRegistrationReceipt? Receipt { get; init; }

    /// <summary>Gets the server registration requirement; the default requires compatible wire identity.</summary>
    public RpcRegistrationRequirement Requirement { get; init; }
}
