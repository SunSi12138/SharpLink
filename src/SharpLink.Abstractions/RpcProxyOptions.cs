namespace SharpLink.Abstractions;

/// <summary>Immutable construction options captured by an identity-bearing proxy or channel.</summary>
public sealed class RpcProxyOptions
{
    /// <summary>Gets the required remote registration predicate.</summary>
    public RpcBindingRequirement Requirement { get; init; }

    /// <summary>Gets the full original receipt; required for minimum-version and exact-registration binding.</summary>
    public RpcRegistrationReceipt? Receipt { get; init; }

    /// <summary>Gets the explicit cluster scope, or null for the owning client's captured scope.</summary>
    /// <remarks>Null never means a later route lookup or a global authority.</remarks>
    public SharpLinkClusterKey? TargetScope { get; init; }

    /// <summary>Gets the immutable ordered metadata snapshot attached to each invocation.</summary>
    public SharpLinkMetadata? Metadata { get; init; }

    internal void Validate()
    {
        if (Requirement is not (RpcBindingRequirement.Compatible or
            RpcBindingRequirement.MinimumVersion or RpcBindingRequirement.ExactRegistration))
            throw new ArgumentOutOfRangeException(nameof(Requirement));
        if (TargetScope is { } scope && !SharpLinkClusterKey.IsValid(scope.Value))
            throw new ArgumentException("A valid target cluster scope is required.", nameof(TargetScope));
        if (Receipt is { } receipt && !receipt.IsValid)
            throw new ArgumentException("A complete valid registration receipt is required.", nameof(Receipt));
        if (Requirement != RpcBindingRequirement.Compatible && Receipt is null)
            throw new ArgumentException("This binding requirement requires a registration receipt.", nameof(Receipt));
    }

    internal void ValidateFor(RpcLocalContract local)
    {
        ArgumentNullException.ThrowIfNull(local);
        Validate();
        if (Receipt is { } receipt &&
            (receipt.ContractId != local.ContractId || receipt.RpcAssemblyHash != local.RpcAssemblyHash))
            throw new ArgumentException("The receipt must identify the exact selected local contract and hash.", nameof(Receipt));
    }
}
