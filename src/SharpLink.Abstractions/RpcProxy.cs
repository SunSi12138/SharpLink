using System;

namespace SharpLink.Abstractions;

/// <summary>Identifies one client-issued, immutable local registration.</summary>
public sealed class RpcLocalContract
{
    internal RpcLocalContract(Guid issuer, long generation, long contractId, RpcHash128 hash)
        => (Issuer, Generation, ContractId, Hash) = (issuer, generation, contractId, hash);

    internal Guid Issuer { get; }
    internal long Generation { get; }
    internal long ContractId { get; }
    internal RpcHash128 Hash { get; }
}

/// <summary>Captures the fixed metadata for a locally created proxy.</summary>
public sealed class RpcProxyOptions
{
    /// <summary>Gets or initializes the immutable metadata attached to calls.</summary>
    public SharpLinkMetadata? Metadata { get; init; }

    /// <summary>Gets the optional scalar registration receipt, scoped to one server and epoch.</summary>
    public RpcRegistrationReceipt? Receipt { get; init; }

    /// <summary>Gets the immutable registration constraint applied during binding.</summary>
    public RpcRegistrationRequirement Requirement { get; init; }
}

/// <summary>Defines the cold binding constraint.</summary>
public enum RpcRegistrationRequirement
{
    /// <summary>Requires contract/hash compatibility and, with a receipt, the same server/epoch.</summary>
    Compatible,
    /// <summary>Also requires the registration's own publication generation to meet the receipt.</summary>
    MinimumVersion,
    /// <summary>Also requires the precise registration ID captured by the receipt.</summary>
    ExactRegistration
}

/// <summary>Identifies one server registration using its contract-owner hash and service publication identity; it conveys no authorization.</summary>
public readonly record struct RpcRegistrationReceipt(Guid ServerInstanceId, Guid RegistryEpoch,
    long Generation, long ContractId, RpcHash128 RpcAssemblyHash, long RegistrationId);

/// <summary>Pairs the generated service with preparation of precisely its captured channel.</summary>
public sealed class RpcProxy<TContract> where TContract : IService
{
    private readonly IRpcContractChannel _channel;

    internal RpcProxy(TContract service, IRpcContractChannel channel)
        => (Service, _channel) = (service, channel);

    /// <summary>Gets the generated service; invocation does not go through this handle.</summary>
    public TContract Service { get; }

    /// <summary>Waits within one finite budget for this channel's confirmed binding.</summary>
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _channel.PrepareAsync(timeout, cancellationToken);
}
