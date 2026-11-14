namespace SharpLink.Abstractions;

/// <summary>Pairs one server registration outcome with receipts captured by that publication.</summary>
/// <remarks>Receipts contain host scalars only. They neither reserve calls nor prove that a registration still accepts calls.</remarks>
public sealed class SharpLinkServerAssemblyRegistrationResult
{
    internal SharpLinkServerAssemblyRegistrationResult(SharpLinkAssemblyRegistrationResult registration,
        IReadOnlyList<RpcRegistrationReceipt> receipts)
        => (Registration, Receipts) = (registration, receipts);

    /// <summary>Gets the original structured registration outcome.</summary>
    public SharpLinkAssemblyRegistrationResult Registration { get; }

    /// <summary>Gets one immutable receipt per service published by this operation, ordered by ContractId; empty on rejection or a contract-only publication.</summary>
    public IReadOnlyList<RpcRegistrationReceipt> Receipts { get; }
}

/// <summary>Pairs one server replacement outcome with receipts from its new registration publication.</summary>
/// <remarks>Later replacements cannot change these receipts. Old-registration cleanup and caller cancellation retain the existing replacement semantics.</remarks>
public sealed class SharpLinkServerAssemblyReplacementResult
{
    internal SharpLinkServerAssemblyReplacementResult(SharpLinkAssemblyReplacementResult replacement,
        IReadOnlyList<RpcRegistrationReceipt> receipts)
        => (Replacement, Receipts) = (replacement, receipts);

    /// <summary>Gets the original publication and old-registration drain outcome.</summary>
    public SharpLinkAssemblyReplacementResult Replacement { get; }

    /// <summary>Gets one immutable receipt per service from this operation's new publication, ordered by ContractId; empty on rejection or a contract-only publication.</summary>
    public IReadOnlyList<RpcRegistrationReceipt> Receipts { get; }
}
