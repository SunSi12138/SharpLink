using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractDependencyTests
{
    private const string MissingRpcCodecDependency =
        "SharpLink.Missing.RpcCodec.Dependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    [Test]
    public async Task DynamicDependencyValidationShouldIncludeContractDependencies()
    {
        await using var client = SharpClientBuilder.Create()
            .UseTcp("127.0.0.1", 1)
            .Build();
        var implementation = client.GetType();
        var validate = implementation.GetMethod(
            "ValidateDependencies",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client dependency validator was not found.");
        var dynamicModuleType = validate.GetParameters()[1].ParameterType.GetElementType()
            ?? throw new InvalidOperationException("Client dynamic module element type was not found.");
        var emptyModules = Array.CreateInstance(dynamicModuleType, 0);

        var error = (SharpLinkAssemblyRegistrationError?)validate.Invoke(
            client,
            [new ContractDependencyOnlyManifest(), emptyModules]);

        Ensure(error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "a missing RPC-only Contract dependency must reject Client dynamic registration");
        Ensure(error?.Message.Contains(MissingRpcCodecDependency, StringComparison.Ordinal) == true,
            "the Client dependency diagnostic must identify the missing RPC-only dependency");
    }

    private sealed class ContractDependencyOnlyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ContractDependencyOnlyManifest).Assembly;
        public string CompileTimeDescriptor => string.Empty;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<string> ContractDependencies => [MissingRpcCodecDependency];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
