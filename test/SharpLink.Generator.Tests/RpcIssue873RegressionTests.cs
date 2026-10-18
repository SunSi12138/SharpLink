using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue873GenericElementArrayRanksMustAppearOutsideGenericArguments()
    {
        foreach (var type in new[]
        {
            "System.Collections.Generic.List<string[]>[][]",
            "System.Collections.Generic.Dictionary<string, string[]>[][]",
            "System.Memory<System.Collections.Generic.List<string[]>[]>",
            "System.Collections.Immutable.ImmutableArray<System.Collections.Generic.List<string[]>[]>",
            "string[][][]",
            "System.Collections.Generic.List<string>[][]"
        })
        {
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IArrayContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<{{type}}> Get();
}
""");
            var result = GeneratorTestHarness.Run("ArrayRankRegression", source);
            Ensure(!result.Diagnostics.Any(d => d.Id == "CS8785"), $"generator failed for {type}");
            var generated = result.GeneratedTrees.Select(t => t.GetText().ToString()).ToArray();
            Ensure(generated.Length > 0, $"expected generated codecs for {type}");
            var errors = generated.SelectMany(t => CSharpSyntaxTree.ParseText(t).GetDiagnostics())
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Ensure(errors.Length == 0,
                $"generated syntax for {type} is invalid: {string.Join("; ", errors.Select(d => d.ToString()))}");
        }
        return Task.CompletedTask;
    }
}
