using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue805GeneratorDataModelsShouldBeSealedButKeepValueSemantics()
    {
        // The generator analysis records are intentionally invisible to external
        // assemblies, including this test. Inspect their emitted metadata instead
        // of widening InternalsVisibleTo solely to let the test compile.
        foreach (var name in new[]
        {
            "RpcServiceModel",
            "RpcConstructorParameterModel",
            "RpcInterfaceModel",
            "RpcMethodModel",
            "RpcParameterModel"
        })
        {
            var type = typeof(RpcGenerator).Assembly.GetType(
                "SharpLink.Generator." + name, throwOnError: true);
            Ensure(type is { IsNotPublic: true, IsSealed: true },
                $"Generator data model {name} must remain internal and non-inheritable.");
        }

        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IImmutableModelControl : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Read();
}
""");
        var first = string.Join("\n", RunGeneratorAndGetSources(source));
        var second = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(first.Contains("IImmutableModelControl", StringComparison.Ordinal) && first == second,
            "Sealing internal analysis models must not perturb the generator output or its determinism.");
        return Task.CompletedTask;
    }
}
