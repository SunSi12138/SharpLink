using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class ClientShutdownDependencyOrderTests
{
    [Test]
    public void ShutdownShouldReleaseDependantsBeforeDependencies()
    {
        // Use real, already loaded assembly identities so the generated dependency binder
        // exercises the same AssemblyLoadContext resolution as the production shutdown path.
        var moduleB = typeof(SharpLinkException).Assembly;
        var moduleA = typeof(SharpLinkRuntimeContext).Assembly;
        var moduleC = typeof(SharpLinkClient).Assembly;
        ISharpLinkGeneratedAssemblyManifest[] manifests =
        [
            new ShutdownTestManifest(moduleB, []),
            new ShutdownTestManifest(moduleA, [moduleB.FullName!]),
            new ShutdownTestManifest(moduleC, [moduleA.FullName!])
        ];

        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        Ensure(order.SequenceEqual([2, 1, 0]),
            "shutdown must release ModuleC before ModuleA before ModuleB so the normal unregister dependant guard remains valid");
    }

    [Test]
    public void ShutdownOrderShouldRemainDeterministicForIndependentModules()
    {
        ISharpLinkGeneratedAssemblyManifest[] manifests =
        [
            new ShutdownTestManifest(typeof(SharpLinkException).Assembly, []),
            new ShutdownTestManifest(typeof(SharpLinkRuntimeContext).Assembly, []),
            new ShutdownTestManifest(typeof(SharpLinkClient).Assembly, [])
        ];

        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        Ensure(order.SequenceEqual([0, 1, 2]),
            "independent dynamic modules should retain deterministic registration-snapshot order during shutdown");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ShutdownTestManifest(
        Assembly ownerAssembly,
        IReadOnlyList<string> dependencies) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "shutdown-dependency-order-test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "shutdown-order-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies { get; } = dependencies;
    }
}
