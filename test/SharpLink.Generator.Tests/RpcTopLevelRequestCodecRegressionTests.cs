using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ExplicitBuiltinCodecShouldOwnTopLevelRequestWire()
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

        var generated = RunGeneratorAndGetSources(source);
        var requestHelper = generated.Single(static text =>
            text.Contains("internal readonly struct", StringComparison.Ordinal) &&
            text.Contains("__codec_value", StringComparison.Ordinal));
        var stub = generated.Single(static text =>
            text.Contains("private sealed class __Stub_", StringComparison.Ordinal));

        Ensure(requestHelper.Contains("__codec_value = codecs.GetCodec<int>();", StringComparison.Ordinal),
            "a top-level fixed request value with an explicit final Codec must acquire that Codec in the generated request helper");
        Ensure(requestHelper.Contains("__codec_value.Serialize(value.value, writer);", StringComparison.Ordinal),
            "proxy request serialization must invoke the explicit final Codec instead of the CLR fixed-type fast path");
        Ensure(requestHelper.Contains("var value_value = __codec_value.Deserialize(payload_value);", StringComparison.Ordinal),
            "the generated request helper must deserialize the value through the explicit final Codec");
        Ensure(!requestHelper.Contains("Unsafe.WriteUnaligned", StringComparison.Ordinal) &&
               !requestHelper.Contains("Unsafe.ReadUnaligned<int>", StringComparison.Ordinal),
            "the generated request helper must not retain a native inline bypass once the final binding is non-Native");

        Ensure(stub.Contains("codecs.GetCodec<int>()", StringComparison.Ordinal) &&
               stub.Contains(".Deserialize(in seq_value)", StringComparison.Ordinal),
            "server request dispatch must resolve and invoke the same explicit final Codec for the top-level argument");
        Ensure(!stub.Contains("Unsafe.ReadUnaligned<int>", StringComparison.Ordinal),
            "server request dispatch must not decode an explicitly coded top-level int through the native inline path");
        return Task.CompletedTask;
    }
}
