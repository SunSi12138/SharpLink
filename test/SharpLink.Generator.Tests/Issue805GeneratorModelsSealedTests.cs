using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue805GeneratorDataModelsShouldBeSealedButKeepValueSemantics()
    {
        foreach (var type in new[]
        {
            typeof(RpcServiceModel),
            typeof(RpcConstructorParameterModel),
            typeof(RpcInterfaceModel),
            typeof(RpcMethodModel),
            typeof(RpcParameterModel)
        })
        {
            Ensure(!type.IsPublic && type.IsSealed,
                $"Generator data model {type.Name} must remain internal and non-inheritable.");
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
