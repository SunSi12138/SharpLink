namespace SharpLink.Sdk;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SharpLinkRpcContractsAttribute(params Type[] contractTypes) : Attribute
{
    public Type[] ContractTypes { get; } = contractTypes;
}
