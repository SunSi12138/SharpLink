using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue871NullableTaskMustNotAbortHealthyContractGeneration()
    {
        foreach (var taskReturn in new[] { "Task", "Task?", "Task<int>?", "Task<int?>" })
        {
            var source = BuildSource($$"""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface ITaskCase : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] {{taskReturn}} Run();
}
[SharpLink.Sdk.RpcContract]
public interface ITaskHealthy : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Ping();
}
""");
            var result = GeneratorTestHarness.Run("NullableTaskRegression", source);
            Ensure(!result.Diagnostics.Any(d => d.Id == "CS8785"),
                $"the generator must not crash for {taskReturn}");
            var generated = result.GeneratedTrees.Select(t => t.GetText().ToString()).ToArray();
            Ensure(generated.Any(text => text.Contains("ITaskHealthy", StringComparison.Ordinal)),
                $"an unrelated healthy contract must still be generated for {taskReturn}");
            Ensure(generated.Any(text => text.Contains("ITaskCase", StringComparison.Ordinal)),
                $"the supported task return type {taskReturn} must generate its contract");
        }
        return Task.CompletedTask;
    }
}
