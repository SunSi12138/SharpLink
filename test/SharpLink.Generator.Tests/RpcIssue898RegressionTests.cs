using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue898DefaultReferenceScanMustSeeServicesCompiledAgainstSdkTypeForwarders()
    {
        // Compile real metadata forwarders rather than redefining the SDK surface
        // directly in SharpLink.Sdk (which masks the regression).
        var abstractions = CreateMetadataReference("SharpLink.Abstractions", """
using System;
namespace SharpLink.Sdk
{
    public interface IService { }
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class SharpLinkRpcContractsAttribute : Attribute
    {
        public SharpLinkRpcContractsAttribute(params Type[] types) { }
    }
}
namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(Type type, int api,
            int protocol, string version, string abiIdentity) { }
    }
}
""");
        var sdk = CreateMetadataReference("SharpLink.Sdk", """
using System.Runtime.CompilerServices;
using SharpLink.Sdk;
[assembly: TypeForwardedTo(typeof(IService))]
[assembly: TypeForwardedTo(typeof(RpcContractAttribute))]
[assembly: TypeForwardedTo(typeof(RpcServiceAttribute))]
[assembly: TypeForwardedTo(typeof(NonCancellableAttribute))]
[assembly: TypeForwardedTo(typeof(SharpLinkRpcContractsAttribute))]
""", abstractions);
        var contracts = CreateMetadataReference("Review.Contracts", """
using SharpLink.Sdk;
using System.Threading.Tasks;
namespace Review
{
    [RpcContract]
    public interface IContract : IService
    {
        [NonCancellable] ValueTask<int> Get();
    }
}
""", sdk, abstractions);

        static string Service(string id, int result) => $$"""
using SharpLink.Sdk;
using SharpLink.Abstractions;
using System.Threading.Tasks;
[assembly: SharpLinkGeneratedAssemblyManifest(typeof(SharpLink.Generated.ServiceManifest),
    5, 3, "3.0.0", "sharplink-3.0-api5-contract-channel-complete-method-facts-v1")]
namespace SharpLink.Generated
{
    public sealed class ServiceManifest
    {
        public static void Register() { }
    }
}
namespace Review
{
    [RpcService]
    public sealed class {{id}} : IContract
    {
        public ValueTask<int> Get() => new({{result}});
    }
}
""";
        var serviceA = CreateMetadataReference("Review.ServiceA", Service("ImplementationA", 1),
            abstractions, sdk, contracts);
        var serviceB = CreateMetadataReference("Review.ServiceB", Service("ImplementationB", 2),
            abstractions, sdk, contracts);
        const string consumer = "public sealed class ConsumerMarker { }";
        var consumerCompilation = GeneratorTestHarness.CreateCompilation(
            "ReferenceScanProbe", consumer, [abstractions, sdk, contracts, serviceA, serviceB]);
        foreach (var reference in new[] { contracts, serviceA, serviceB })
        {
            var assembly = consumerCompilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
            Ensure(assembly is not null &&
                   !assembly.Modules.Any(module => module.ReferencedAssemblySymbols.Any(
                       referenced => referenced.Name == "SharpLink.Sdk")),
                "compiled forwarded contract/service assembly must not have an SDK AssemblyRef");
        }

        var diagnostics = RunGenerator(consumer, abstractions, sdk, contracts, serviceA, serviceB);
        Ensure(diagnostics.Count(d => d.Id == "SHARPLINK023") == 1,
            $"default scan must report two service owners of one contract: {FormatDiagnostics(diagnostics)}");
        var one = RunGenerator(consumer, abstractions, sdk, contracts, serviceA);
        Ensure(!one.Any(d => d.Id == "SHARPLINK023"),
            "one referenced service must be accepted");

        var local = RunGenerator("""
[SharpLink.Sdk.RpcService]
public sealed class LocalService : Review.IContract
{
    public System.Threading.Tasks.ValueTask<int> Get() => new(3);
}
""", abstractions, sdk, contracts, serviceA);
        Ensure(local.Any(d => d.Id == "SHARPLINK023"),
            "default scan must detect one local + one referenced service");

        foreach (var filter in new[]
        {
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts()]",
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts(typeof(Review.ImplementationA))]"
        })
        {
            var filtered = RunGenerator(filter + "\n" + consumer, abstractions, sdk, contracts, serviceA, serviceB);
            Ensure(!filtered.Any(d => d.Id == "SHARPLINK023"),
                "explicit empty/single-assembly selection must retain its filter semantics");
        }
        return Task.CompletedTask;
    }
}
