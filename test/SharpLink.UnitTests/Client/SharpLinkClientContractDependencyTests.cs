using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractDependencyTests
{
    private const string MissingRpcCodecDependency =
        "SharpLink.Missing.RpcCodec.Dependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string StaleAbiIdentity = "sharplink-2.0-api4-rpcchannel-metadata-v2";

    [Test]
    public async Task DynamicDependencyValidationShouldIncludeContractDependencies()
    {
        await using var client = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout()
            .UseTcp("127.0.0.1", 1)
            .Build();
        var registry = GetAssemblyRegistry(client);
        var validate = typeof(ClientAssemblyRegistry).GetMethod(
            "ValidateDependencies",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client dependency validator was not found.");
        var dynamicModuleType = validate.GetParameters()[1].ParameterType.GetElementType()
            ?? throw new InvalidOperationException("Client dynamic module element type was not found.");
        var emptyModules = Array.CreateInstance(dynamicModuleType, 0);

        var error = (SharpLinkAssemblyRegistrationError?)validate.Invoke(
            registry,
            [new TestManifest(typeof(TestManifest).Assembly, [MissingRpcCodecDependency]), emptyModules]);

        Ensure(error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "a missing RPC-only Contract dependency must reject Client dynamic registration");
        Ensure(error?.Message.Contains(MissingRpcCodecDependency, StringComparison.Ordinal) == true,
            "the Client dependency diagnostic must identify the missing RPC-only dependency");
    }

    [Test]
    public async Task ClientUnregisterShouldProtectContractDependencies()
    {
        await using var client = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout()
            .UseTcp("127.0.0.1", 1)
            .Build();
        var registry = GetAssemblyRegistry(client);
        var modulesField = typeof(ClientAssemblyRegistry).GetField(
            "_dynamicModules",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client dynamic module registry was not found.");
        var modules = (IDictionary)(modulesField.GetValue(registry)
            ?? throw new InvalidOperationException("Client dynamic module registry was null."));
        var dynamicModuleType = modulesField.FieldType.GetGenericArguments()[1];
        var constructor = dynamicModuleType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static ctor => ctor.GetParameters().Length == 3);
        var ensureNoDependants = typeof(ClientAssemblyRegistry).GetMethod(
            "EnsureNoDynamicDependants",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client unregister dependency guard was not found.");

        var dependencyAssembly = typeof(IService).Assembly;
        var dependantAssembly = client.GetType().Assembly;
        var dependencyManifest = new TestManifest(dependencyAssembly, []);
        var dependantManifest = new TestManifest(
            dependantAssembly,
            [dependencyAssembly.FullName!]);
        var dependencyModule = constructor.Invoke([dependencyAssembly, dependencyManifest, null]);
        var dependantModule = constructor.Invoke([dependantAssembly, dependantManifest, null]);

        modules.Add(dependencyAssembly, dependencyModule);
        modules.Add(dependantAssembly, dependantModule);
        try
        {
            try
            {
                ensureNoDependants.Invoke(registry, [dependencyModule]);
                throw new InvalidOperationException(
                    "Client unregister accepted a module that still has a Contract-only dependant.");
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is InvalidOperationException)
            {
            }
        }
        finally
        {
            modules.Clear();
        }
    }

    [Test]
    public async Task StaleApi4DescriptorAbiShouldBeRejectedBeforeManifestActivation()
    {
        await using var client = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout()
            .UseTcp("127.0.0.1", 1)
            .Build();
        var assemblyName = new AssemblyName(
            "SharpLink.StaleGeneratedAbi." + Guid.NewGuid().ToString("N"));
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var locatorConstructor = typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])
            ?? throw new InvalidOperationException("Current manifest locator constructor was not found.");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            locatorConstructor,
            [
                typeof(TestManifest),
                SharpLinkGeneratedManifestVersions.Api,
                SharpLinkGeneratedManifestVersions.Protocol,
                "stale-test",
                StaleAbiIdentity
            ]));

        var result = client.RegisterAssembly(assembly);

        Ensure(!result.Succeeded,
            "a stale API-4 generated descriptor ABI must not be accepted by the current runtime");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            "a stale API-4 ABI locator must be rejected at the compatibility boundary");
        Ensure(result.Error?.Message.Contains(StaleAbiIdentity, StringComparison.Ordinal) == true,
            "the stale ABI diagnostic must identify the rejected generated ABI identity");
    }

    private static ClientAssemblyRegistry GetAssemblyRegistry(ISharpLinkClient client)
        => (ClientAssemblyRegistry)(typeof(SharpLinkClient).GetField(
                "_assemblyRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client)
            ?? throw new InvalidOperationException("Client assembly registry was not found."));

    private sealed class TestManifest(
        Assembly ownerAssembly,
        IReadOnlyList<string> contractDependencies) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => ownerAssembly;
        public string CompileTimeDescriptor => string.Empty;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<string> ContractDependencies => contractDependencies;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
