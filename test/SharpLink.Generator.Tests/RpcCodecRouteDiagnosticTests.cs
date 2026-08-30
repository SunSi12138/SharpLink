using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task DiagnoseIndirectAllRouteBinding()
    {
        var thirdParty = CreateMetadataReference(
            "ThirdParty.Indirect.Diagnostic",
            """
namespace Vendor
{
    public sealed class ExternalGraph { public string Name { get; set; } = string.Empty; }
    public struct ExternalPoint { public int X; public int Y; }
}
""");
        var source = AddAssemblyAttributes(BuildRouteSource("""
public sealed class Envelope
{
    public Vendor.ExternalGraph Graph { get; set; } = new();
    public Vendor.ExternalPoint Point { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IEnvelopeContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1000000000000005UL, 0x2000000000000005UL)]
public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.all/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.all/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var diagnostics = RunGenerator(source, thirdParty);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, thirdParty));
        var routed = generated.Contains("CreateCodec<global::Envelope>()", StringComparison.Ordinal);
        if (!routed)
        {
            throw new Exception(
                $"indirect all-route diagnostic: nativeEnvelope={generated.Contains("IRpcCodec<global::Envelope>", StringComparison.Ordinal)}; " +
                $"targetFactory={generated.Contains("TargetType => typeof(global::Envelope)", StringComparison.Ordinal)}; " +
                $"adapterId={generated.Contains("route.all/v1", StringComparison.Ordinal)}; " +
                $"contractPolicy={generated.Contains("__SharpLinkGeneratedContractPolicyCodec_", StringComparison.Ordinal)}; " +
                $"diagnostics={FormatDiagnostics(diagnostics)}");
        }
        return Task.CompletedTask;
    }
}
