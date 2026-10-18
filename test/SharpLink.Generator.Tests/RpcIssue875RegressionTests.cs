using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue875OpenContainingTypeMustRejectNestedSerializableDto()
    {
        var source = BuildSource("""
public class OpenContainer<T>
{
    [SharpLink.Sdk.RpcSerializable]
    public sealed class Data { public string Name { get; set; } = ""; }
}
[SharpLink.Sdk.RpcContract]
public interface IHealthyDto : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Ping();
}
""");
        EnsureHasRule(source, "SHARPLINK009");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("global::OpenContainer<T>.Data", StringComparison.Ordinal),
            "open DTO codec must be suppressed instead of emitting undefined T");
        Ensure(generated.Contains("IHealthyDto", StringComparison.Ordinal),
            "independent valid contract must continue generating");
        var closed = BuildSource("""
public class OpenContainer<T>
{
    public sealed class Data { public int Number { get; set; } }
}
[SharpLink.Sdk.RpcContract]
public interface IClosedDto : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<OpenContainer<int>.Data> Read();
}
""");
        EnsureDoesNotHaveRule(closed, "SHARPLINK009");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(closed))
                .Contains("IClosedDto", StringComparison.Ordinal),
            "closed outer generic instantiation must remain supported");
        return Task.CompletedTask;
    }
}
