using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue805GeneratorEntryPointShouldBeSealedWithoutLosingRoslynContract()
    {
        var type = typeof(RpcGenerator);
        Ensure(type.IsPublic && type.IsSealed,
            "The shipping Roslyn generator is a public, sealed entry point, not a subclassing SPI.");
        Ensure(typeof(IIncrementalGenerator).IsAssignableFrom(type),
            "Sealing must preserve the incremental generator interface.");
        Ensure(type.GetConstructor(Type.EmptyTypes) is { IsPublic: true },
            "The Roslyn compiler must still be able to instantiate the generator.");

        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ISurfaceControl : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
""");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(source))
            .Contains("ISurfaceControl", StringComparison.Ordinal),
            "The sealed generator must continue to emit its normal compiled contract artifacts.");
        return Task.CompletedTask;
    }
}
