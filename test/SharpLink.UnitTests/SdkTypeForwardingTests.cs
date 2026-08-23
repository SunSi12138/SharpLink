using System.Linq;

namespace SharpLink.UnitTests;

public class SdkTypeForwardingTests
{
    private static readonly string[] PublishedContractTypes =
    [
        "SharpLink.Sdk.IdempotentAttribute",
        "SharpLink.Sdk.IService",
        "SharpLink.Sdk.NonCancellableAttribute",
        "SharpLink.Sdk.OnewayAttribute",
        "SharpLink.Sdk.RpcCodecAdapterAttribute",
        "SharpLink.Sdk.RpcCodecAdapterRegistrationAttribute",
        "SharpLink.Sdk.RpcCodecRouteAttribute",
        "SharpLink.Sdk.RpcCodecScope",
        "SharpLink.Sdk.RpcContractAttribute",
        "SharpLink.Sdk.RpcIgnoreAttribute",
        "SharpLink.Sdk.RpcMemberAttribute",
        "SharpLink.Sdk.RpcRequiredAttribute",
        "SharpLink.Sdk.RpcSerializableAttribute",
        "SharpLink.Sdk.RpcServiceAttribute",
        "SharpLink.Sdk.RpcUnionCaseAttribute",
        "SharpLink.Sdk.SharpLinkCallOptions",
        "SharpLink.Sdk.SharpLinkClusterContractAssemblyAttribute",
        "SharpLink.Sdk.SharpLinkMetadata",
        "SharpLink.Sdk.SharpLinkRpcContractsAttribute",
        "SharpLink.Sdk.SharpLinkServiceLifetime",
        "SharpLink.Sdk.TimeoutAttribute"
    ];

    [Test]
    public void SdkAssemblyShouldForwardEveryPublishedContractTypeToAbstractions()
    {
        var sdkAssembly = System.Reflection.Assembly.Load("SharpLink.Sdk");
        var forwardedTypes = sdkAssembly.GetForwardedTypes()
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);

        Ensure(forwardedTypes.Count == PublishedContractTypes.Length,
            "SDK should expose exactly the published contract types as forwarders");

        foreach (var typeName in PublishedContractTypes)
        {
            if (!forwardedTypes.TryGetValue(typeName, out var forwardedType))
            {
                throw new InvalidOperationException($"SDK should forward {typeName}");
            }

            Ensure(forwardedType.Assembly.GetName().Name == "SharpLink.Abstractions",
                $"{typeName} should resolve from SharpLink.Abstractions");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
