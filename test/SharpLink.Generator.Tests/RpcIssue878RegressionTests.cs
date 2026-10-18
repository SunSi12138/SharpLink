using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue878RequiredMembersShouldReportConstructorDiagnostic()
    {
        foreach (var member in new[]
        {
            "public required string Name { get; init; }",
            "public required string Name;"
        })
        {
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IRequiredService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
[SharpLink.Sdk.RpcService]
public sealed class RequiredService : IRequiredService
{
    {{member}}
    public ValueTask<int> Get() => new(1);
}
""");
            EnsureHasRuleContaining(source, "SHARPLINK019", "required");
            var manifest = GetGeneratedManifest(source);
            Ensure(!manifest.Contains("typeof(global::RequiredService)", StringComparison.Ordinal),
                "unconstructible service must not be registered in the generated manifest");
        }
        var supported = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IRequiredService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
[SharpLink.Sdk.RpcService]
public sealed class RequiredService : IRequiredService
{
    public required string Name { get; init; }
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RequiredService() { Name = ""; }
    public ValueTask<int> Get() => new(1);
}
""");
        EnsureDoesNotHaveRule(supported, "SHARPLINK019");
        Ensure(GetGeneratedManifest(supported).Contains("typeof(global::RequiredService)", StringComparison.Ordinal),
            "constructor with SetsRequiredMembers must remain supported");
        return Task.CompletedTask;
    }
}
