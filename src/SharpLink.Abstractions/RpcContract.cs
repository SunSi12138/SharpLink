namespace SharpLink.Abstractions;

/// <summary>Selects a registration using its captured publication identity.</summary>
public enum RpcRegistrationRequirement
{
    /// <summary>Requires the exact contract assembly hash.</summary>
    Compatible = 0,
    /// <summary>Requires at least the receipt's publication generation.</summary>
    MinimumVersion = 1,
    /// <summary>Requires the receipt's exact registration.</summary>
    ExactRegistration = 2
}

/// <summary>Identifies one committed service registration without retaining its module.</summary>
public readonly record struct RpcRegistrationReceipt(
    Guid ServerInstanceId, Guid RegistryEpoch, long Generation,
    long ContractId, RpcHash128 RpcAssemblyHash, long RegistrationId);

/// <summary>Captures immutable construction-time contract options.</summary>
public sealed class RpcProxyOptions
{
    /// <summary>Gets the fixed metadata overridden by non-null per-call metadata.</summary>
    public SharpLinkMetadata? Metadata { get; init; }
    /// <summary>Gets the selected publication receipt.</summary>
    public RpcRegistrationReceipt? Receipt { get; init; }
    /// <summary>Gets the registration comparison rule.</summary>
    public RpcRegistrationRequirement Requirement { get; init; }
}

/// <summary>A client-issued scalar selector for one exact local contract generation.</summary>
public sealed class RpcLocalContract
{
    internal RpcLocalContract(Guid issuer, long generation, long contractId, RpcHash128 hash)
    { Issuer = issuer; Generation = generation; ContractId = contractId; Hash = hash; }
    internal Guid Issuer { get; }
    internal long Generation { get; }
    internal long ContractId { get; }
    internal RpcHash128 Hash { get; }
}

/// <summary>Pairs a generated service and its exact contract site.</summary>
public sealed class RpcProxy<TContract> where TContract : IService
{
    internal RpcProxy(TContract service, IRpcContractChannel channel)
    { Service = service; _channel = channel; }
    private readonly IRpcContractChannel _channel;
    /// <summary>Gets the generated contract service.</summary>
    public TContract Service { get; }
    /// <summary>Waits for this site's readiness within a finite budget.</summary>
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _channel.PrepareAsync(timeout, cancellationToken);
}
