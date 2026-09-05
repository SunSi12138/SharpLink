using System;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void CurrentSharpPackableContextDependenciesShouldGenerateExternalSidecar()
    {
        var vendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalChild
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
""");
        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<SourceSharpPackRoot> EchoAsync(
        SourceSharpPackRoot request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
[global::SharpPack.SharpPackable]
public partial class SourceSharpPackRoot
{
    [global::SharpPack.SharpPackAllowSerialize]
    public global::Vendor.ExternalChild? Child { get; set; }

    public global::System.Collections.Generic.List<global::Vendor.ExternalChild>? Children { get; set; }
}
""");

        var result = RunSharpPackAndCompile(
            "SharpPackCurrentGeneratedDependency",
            source,
            [vendor]);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "context-resolved external child receives a generated sidecar");
        Ensure(generated.Contains(
                "builder.Register<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "external child sidecar is registered into the generated SharpPack scope");
        Ensure(!generated.Contains(
                "SharpPackFormatter<global::SourceSharpPackRoot>",
                StringComparison.Ordinal),
            "the current-compilation SharpPack-generated root remains owned by SharpPack");
    }
}
