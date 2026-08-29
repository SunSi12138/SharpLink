using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task UnsafeBlitFixedBufferLengthShouldBreakCompatibility()
    {
        static string Source(int length) => BuildSource($$"""
public unsafe struct RawFixedBuffer
{
    public fixed int Values[{{length}}];
}

[SharpLink.Sdk.RpcContract]
public interface IFixedBufferContract : SharpLink.Sdk.IService
{
    ValueTask<RawFixedBuffer> Echo(RawFixedBuffer value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source(4)).Json;
        var changed = RunContractGenerator(Source(8), baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing only a source fixed-buffer length must change the UnsafeBlit compatibility schema");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsafeBlitSameTypedFieldReorderShouldBreakCompatibility()
    {
        static string Source(string first, string second) => BuildSource($$"""
public struct RawPair
{
    public int {{first}};
    public int {{second}};
}

[SharpLink.Sdk.RpcContract]
public interface IRawPairContract : SharpLink.Sdk.IService
{
    ValueTask<RawPair> Echo(RawPair value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source("Left", "Right")).Json;
        var changed = RunContractGenerator(Source("Right", "Left"), baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "swapping two same-typed raw fields must change the UnsafeBlit compatibility schema");
        return Task.CompletedTask;
    }

    [Test]
    public Task ArbitraryUnmanagedNullableShouldPublishUnsafeBlitIdentity()
    {
        var source = BuildSource("""
public struct ReviewPoint
{
    public int X;
    public int Y;
}

[SharpLink.Sdk.RpcContract]
public interface INullablePointContract : SharpLink.Sdk.IService
{
    ValueTask<ReviewPoint?> Echo(ReviewPoint? value, CancellationToken cancellationToken);
}
""");

        var root = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(source).Json)!.AsObject();
        var method = root["contracts"]!.AsArray()
            .Single()!.AsObject()["methods"]!.AsArray()
            .Single()!.AsObject();
        var requestType = method["request"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "value")["type"]!
            .GetValue<string>();
        var responseType = method["response"]!.AsObject()["type"]!.GetValue<string>();
        Ensure(string.Equals(requestType, responseType, StringComparison.Ordinal),
            "nullable request and response must use the same canonical manifest type identity");

        var nullableCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(item => string.Equals(
                item["type"]!.GetValue<string>(),
                requestType,
                StringComparison.Ordinal));

        Ensure(nullableCodec["kind"]!.GetValue<string>() == "UnsafeBlit",
            "an unmanaged nullable without a generated Nullable factory must publish the runtime UnsafeBlit kind");
        Ensure(!string.IsNullOrWhiteSpace(nullableCodec["schemaId"]!.GetValue<string>()),
            "the unmanaged nullable must carry the whole-value UnsafeBlit layout schema");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedTupleAliasesShouldUseCanonicalExplicitPolicyLookup()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INestedTuplePolicyContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(
        List<(int X, int Y)> value,
        CancellationToken cancellationToken);
}

public sealed class NestedTupleAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "nested-tuple/v1";
    public string WireFormatId => "nested-tuple-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NestedTupleAdapter), \"nested-tuple/v1\", \"nested-tuple-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<ValueTuple<int, int>>), typeof(NestedTupleAdapter))]");

        var root = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(source).Json)!.AsObject();
        var listCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item =>
            {
                var type = item["type"]!.GetValue<string>();
                return type.Contains("List", StringComparison.Ordinal) &&
                       type.Contains("ValueTuple", StringComparison.Ordinal) &&
                       type.Contains("int", StringComparison.Ordinal);
            });

        Ensure(listCodec["kind"]!.GetValue<string>() == "Adapter",
            "the explicit whole-List binding must match a consumed List with named nested tuple aliases");
        Ensure(listCodec["wireFormatId"]!.GetValue<string>() == "nested-tuple-wire/v1",
            "nested tuple source names must not make explicit policy lookup fall back to the native List Codec");
        return Task.CompletedTask;
    }
}
