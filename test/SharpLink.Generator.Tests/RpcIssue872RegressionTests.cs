using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue872NullableFixedGetterMustBeCapturedOncePerField()
    {
        foreach (var includeString in new[] { false, true })
        {
            var label = includeString ? "[SharpLink.Sdk.RpcMember(6)] public string Label { get; set; } = \"ok\";" : "";
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
public sealed class NullableReading
{
    public static int Reads;
    [SharpLink.Sdk.RpcMember(4)]
    public int? Number { get => ++Reads == 1 ? 19 : null; set { } }
    {{label}}
}
[SharpLink.Sdk.RpcContract]
public interface INullableReadingContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<NullableReading> Read();
}
""");
            var generated = string.Join("\n", RunGeneratorAndGetSources(source));
            if (includeString)
                Ensure(generated.Contains("var __nullable_0 = value.Number;", StringComparison.Ordinal),
                    "cached path must still capture the property once");
            else
            {
                Ensure(generated.Contains("var __nullableValue_4 = value.Number;", StringComparison.Ordinal),
                    "non-cached nullable field must capture its getter before checking HasValue");
                Ensure(generated.Contains("__nullableValue_4.HasValue", StringComparison.Ordinal) &&
                       generated.Contains("__nullableValue_4.Value", StringComparison.Ordinal),
                    "presence and value must refer to the same captured nullable");
            }
        }
        return Task.CompletedTask;
    }
}
