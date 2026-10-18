using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue863CompletedValueTaskShouldBeConsumedForUnaryAndOneWay()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ICompletedTasks : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask Execute();
    [SharpLink.Sdk.Oneway]
    ValueTask Notify();
}
""");
        var result = GeneratorTestHarness.Run("CompletedTasksRegression", source);
        Ensure(result.Diagnostics.All(d => d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            "ValueTask contracts must be accepted");
        var stub = result.GeneratedTrees.Select(t => t.GetText().ToString())
            .Single(t => t.Contains("private sealed class __Stub_", StringComparison.Ordinal));
        Ensure(CountOccurrences(stub, "pending.GetAwaiter().GetResult();") == 2,
            "both unary and OneWay non-generic ValueTask fast paths must consume the original ValueTask");
        Ensure(CountOccurrences(stub, "return pending;") == 2,
            "incomplete, canceled and faulted ValueTasks must still be returned for consumption by the caller");
        return Task.CompletedTask;
    }
}
