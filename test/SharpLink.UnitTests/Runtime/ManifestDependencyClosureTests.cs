using System.Collections.Generic;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public class ManifestDependencyClosureTests
{
    [Test]
    public void DynamicLifecycleDependencyClosureShouldIncludeContractCodecSetDependencies()
    {
        var manifest = new ContractDependencyManifest();
        VerifyClosure(
            typeof(SharpLink.Client.SharpClientBuilder).Assembly.GetType("SharpLink.Client.SharpLinkClient", throwOnError: true)!,
            manifest);
        VerifyClosure(
            typeof(SharpLink.Server.SharpLinkServerBuilder).Assembly.GetType("SharpLink.Server.SharpLinkServer", throwOnError: true)!,
            manifest);
    }

    private static void VerifyClosure(Type ownerType, SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var enumerate = ownerType.GetMethod(
            "EnumerateManifestDependencies",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception($"{ownerType.FullName} dependency enumerator was not found.");
        var dependencies = ((IEnumerable<string>)enumerate.Invoke(null, [manifest])!).ToArray();
        Ensure(dependencies.Contains("Global.Dependency", StringComparer.Ordinal),
            $"{ownerType.FullName} must keep ordinary generated dependencies");
        Ensure(dependencies.Contains("Policy.Dependency", StringComparer.Ordinal),
            $"{ownerType.FullName} must include ContractCodecSet dependencies in dynamic lifecycle guards");

        var dependsOn = ownerType.GetMethod(
            "ManifestDependsOn",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception($"{ownerType.FullName} dependency predicate was not found.");
        Ensure((bool)dependsOn.Invoke(null, [manifest, "Policy.Dependency"])!,
            $"{ownerType.FullName} replacement/unregister guard must see Contract-only dependencies");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ContractDependencyManifest : SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLink.Abstractions.SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLink.Abstractions.SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ManifestDependencyClosureTests).Assembly;
        public string CompileTimeDescriptor => "dependency-closure-test";
        public IReadOnlyList<SharpLink.Abstractions.SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLink.Abstractions.SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<SharpLink.Abstractions.IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => ["Global.Dependency"];
        public IReadOnlyList<SharpLink.Abstractions.SharpLinkGeneratedContractCodecSet> ContractCodecSets =>
        [
            new(
                typeof(ManifestDependencyClosureTests),
                HasCompileTimePolicy: true,
                Codecs: Array.Empty<SharpLink.Abstractions.IRpcGeneratedCodecFactory>(),
                Dependencies: ["Policy.Dependency"])
        ];
    }
}
