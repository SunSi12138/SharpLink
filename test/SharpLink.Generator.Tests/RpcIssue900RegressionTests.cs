using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void Issue900RefOutVendorConstructorsShouldFailClosedWithSlsp0001()
    {
        foreach (var keyword in new[] { "ref", "out" })
        {
            foreach (var secondParameter in new[] { false, true })
            {
                var arguments = secondParameter ? $"string name, {keyword} int id" : $"{keyword} string name";
                var body = secondParameter
                    ? keyword == "out" ? "Name = name; Id = id = 1;" : "Name = name; Id = id;"
                    : keyword == "out" ? "Name = name = \"constructed\";" : "Name = name;";
                var vendor = CreateSharpPackVendorReference($$"""
    namespace Vendor;
    public sealed class ExternalRequest
    {
        public string Name { get; }
        public int Id { get; }
        public ExternalRequest({{arguments}}) { {{body}} }
    }
    """);
                var source = BuildSharpPackContractSource("""
        global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
            Vendor.ExternalRequest request,
            global::System.Threading.CancellationToken cancellationToken);
    """);
                var result = RunSharpPackAndCompile("SharpPackRefOutConstructor", source, [vendor]);
                var diagnostic = result.DriverDiagnostics.FirstOrDefault(d => d.Id == "SLSP0001");
                Ensure(diagnostic is not null && diagnostic.GetMessage().Contains("ref/out", StringComparison.Ordinal),
                    $"unsupported {keyword} must receive actionable SLSP0001, second={secondParameter}");
                var emitted = string.Join("\n", result.DriverRunResult.Results.SelectMany(r => r.GeneratedSources)
                    .Select(r => r.SourceText.ToString()));
                Ensure(!emitted.Contains("value = new global::Vendor.ExternalRequest(", StringComparison.Ordinal),
                    "invalid deserializer constructor call must not be generated");
            }
        }
    }

    [Test]
    public void Issue900ByValueAndInVendorConstructorsRemainSupported()
    {
        foreach (var modifier in new[] { "", "in " })
        {
            var vendor = CreateSharpPackVendorReference($$"""
namespace Vendor;
public sealed class ExternalRequest
{
    public string Name { get; }
    public ExternalRequest({{modifier}}string name) { Name = name; }
}
""");
            var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""");
            var result = RunSharpPackAndCompile("SharpPackSupportedConstructor", source, [vendor]);
            EnsureNoSharpPackErrors(result);
            Ensure(GetSharpPackGeneratedSource(result.DriverRunResult)
                    .Contains("value = new global::Vendor.ExternalRequest(__slsp_0);", StringComparison.Ordinal),
                $"supported {modifier}constructor should still use existing by-value emission");
        }
    }
}
