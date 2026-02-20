namespace SharpLink.Sdk;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SharpLinkRpcContractsAttribute(params Type[] contractTypes) : Attribute
{
    public Type[] ContractTypes { get; } = contractTypes;
}
