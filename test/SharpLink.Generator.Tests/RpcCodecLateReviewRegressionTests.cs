using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task FrameworkPrimitiveElementBindingShouldBeRejectedWithoutChangingCompositeDefaults()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IBuiltinCompositeContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int[]> EchoArray(int[] value, CancellationToken cancellationToken);
    ValueTask<List<int>> EchoList(List<int> value, CancellationToken cancellationToken);
    ValueTask<int?> EchoNullable(int? value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7001UL, 0x8001UL)]
public sealed class CompositeIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "composite-int/v1";
    public string WireFormatId => "composite-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(CompositeIntAdapter), \"composite-int/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(CompositeIntAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK049"),
            "framework primitive int must reject explicit rebinding even when used inside configurable composites");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("composite-int-wire/v1\";", StringComparison.Ordinal),
            "the rejected primitive binding must not enter array/List/Nullable Codec graphs");
        return Task.CompletedTask;
    }

    [Test]
    public Task OpaqueContractCodecShouldStopFinalGraphTraversal()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StandaloneIntEnvelope
{
    public int Value { get; set; }
}

public sealed class OpaqueEnvelope
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7002UL, 0x8002UL)]
public sealed class OpaqueEnvelopeCodec : SharpLink.Abstractions.IRpcCodec<OpaqueEnvelope>
{
}

[SharpLink.Sdk.RpcContract]
public interface IOpaqueEnvelopeContract : SharpLink.Sdk.IService
{
    ValueTask<OpaqueEnvelope> Echo(OpaqueEnvelope value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7003UL, 0x8003UL)]
public sealed class UnrelatedIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "unrelated-int/v1";
    public string WireFormatId => "unrelated-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(OpaqueEnvelope), typeof(OpaqueEnvelopeCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(UnrelatedIntAdapter), \"unrelated-int/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(UnrelatedIntAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK049") == 1,
            "an int hidden below an opaque Contract Codec must not suppress the unrelated standalone builtin override diagnostic");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("new global::OpaqueEnvelopeCodec()", StringComparison.Ordinal),
            "the opaque final Contract Codec must be emitted directly");
        return Task.CompletedTask;
    }
}
