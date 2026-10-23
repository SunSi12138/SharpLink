using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue897FileLocalClusterMarkersMustBeRejectedBeforeEmission()
    {
        foreach (var declaration in new[] { "class", "struct" })
        {
            foreach (var nested in new[] { false, true })
            {
                var invalid = nested
                    ? $"file {declaration} OuterMarker {{ public class Marker {{ }} }}"
                    : $"file {declaration} Marker {{ }}";
                var marker = nested ? "OuterMarker.Marker" : "Marker";
                var source = AddAssemblyAttribute(BuildSource($$"""
    [SharpLink.Sdk.RpcContract]
    public interface IAuditCluster : SharpLink.Sdk.IService
    {
        [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
    }
    {{invalid}}
    """), $"[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"audit\", typeof({marker}))]");
                EnsureHasRule(source, "SHARPLINK041");
                var result = GeneratorTestHarness.Run("FileLocalClusterMarker", source);
                Ensure(!string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()))
                        .Contains($"typeof(global::{marker})", StringComparison.Ordinal),
                    "file-local marker must not leak into a separate generated source tree");
            }
        }

        foreach (var declaration in new[] { "internal class Marker { }", "internal struct Marker { }" })
        {
            var source = AddAssemblyAttribute(BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IAuditCluster : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
{{declaration}}
"""), "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"audit\", typeof(Marker))]");
            EnsureDoesNotHaveRule(source, "SHARPLINK041");
            Ensure(string.Join("\n", RunGeneratorAndGetSources(source))
                .Contains("typeof(global::Marker).Assembly", StringComparison.Ordinal),
                "visible marker must still generate a route");
        }
        return Task.CompletedTask;
    }
}
