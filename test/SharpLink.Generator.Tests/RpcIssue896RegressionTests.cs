using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue896DirectDynamicServiceDependenciesMustBeDiagnosed()
    {
        foreach (var primary in new[] { false, true })
        foreach (var dependency in new[] { "dynamic", "dynamic?", "object", "object?", "dynamic[]", "List<dynamic>" })
        {
            var declaration = primary
                ? $"public sealed class DynamicService({dependency} dependency) : IDynamicService {{ public ValueTask<int> Get() => new(1); }}"
                : $"public sealed class DynamicService : IDynamicService {{ public DynamicService({dependency} dependency) {{ }} public ValueTask<int> Get() => new(1); }}";
            var source = BuildSource($$"""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IDynamicService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
[SharpLink.Sdk.RpcService]
{{declaration}}
""");
            var diagnostics = RunGenerator(source);
            var invalid = dependency is "dynamic" or "dynamic?";
            Ensure(diagnostics.Count(d => d.Id == "SHARPLINK019") == (invalid ? 1 : 0),
                $"expected precise SHARPLINK019 policy for {dependency}, primary={primary}: {FormatDiagnostics(diagnostics)}");
            var manifest = GetGeneratedManifest(source);
            Ensure(manifest.Contains("typeof(global::DynamicService)", StringComparison.Ordinal) != invalid,
                $"invalid dynamic constructor should not be registered, primary={primary}");
            Ensure(!manifest.Contains("typeof(dynamic)", StringComparison.Ordinal),
                "generated manifest must never require typeof(dynamic)");
        }
        return Task.CompletedTask;
    }
}
