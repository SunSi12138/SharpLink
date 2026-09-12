using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ExplicitFrameworkPrimitiveCodecShouldBeRejectedAndKeepNativeRequestWire()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecImplementation("explicit-int-wire/v1", "explicit-int-schema/v1")]
public sealed class ExplicitIntCodec : SharpLink.Abstractions.IRpcCodec<int>
{
}

[SharpLink.Sdk.RpcContract]
public interface IExplicitIntContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(int), typeof(ExplicitIntCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK063"),
            "framework primitive int must reject a custom Codec instead of promoting the fixed request path");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("new global::ExplicitIntCodec()", StringComparison.Ordinal),
            "a rejected primitive custom Codec must not enter generated factories");
        Ensure(generated.Contains("Unsafe.WriteUnaligned", StringComparison.Ordinal) &&
               generated.Contains("Unsafe.ReadUnaligned<int>", StringComparison.Ordinal),
            "the request path must retain the fixed SharpLink int representation");
        return Task.CompletedTask;
    }
}
