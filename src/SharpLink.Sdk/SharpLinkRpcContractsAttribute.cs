namespace SharpLink.Sdk;

/// <summary>Restricts referenced-contract discovery to the assemblies that own the supplied marker types.</summary>
/// <remarks>An explicit empty marker disables referenced-contract discovery.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SharpLinkRpcContractsAttribute(params Type[] contractTypes) : Attribute
{
    public Type[] ContractTypes { get; } = contractTypes;
}
