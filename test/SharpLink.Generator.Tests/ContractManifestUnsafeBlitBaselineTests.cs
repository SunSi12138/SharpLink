using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task DirectUnsafeBlitLayoutChangeShouldFailContractBaseline()
    {
        static string ContractSource(string fieldType) => BuildSource($$"""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RawPayload
{
    public {{fieldType}} Value;
}

[SharpLink.Sdk.RpcContract]
public interface IRawService : SharpLink.Sdk.IService
{
    ValueTask<RawPayload> Echo(RawPayload value, CancellationToken cancellationToken);
}
""");

        var baselineResult = RunContractGenerator(ContractSource("int"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(baselineResult.Json)!.AsObject();
        var method = root["contracts"]!.AsArray().Single()!["methods"]!.AsArray().Single()!.AsObject();
        var request = method["request"]!.AsArray().Single()!.AsObject();
        Ensure(IsValidCodecHashText(request["codecHash"]?.GetValue<string>()),
            "direct UnsafeBlit payload must retain its final CodecHash in the baseline value");
        var rawCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "RawPayload");
        Ensure(rawCodec["kind"]!.GetValue<string>() == "Final",
            "non-emitted final codec leaves must be retained in the complete identity inventory");
        Ensure(IsValidCodecHashText(rawCodec["codecHash"]?.GetValue<string>()),
            "UnsafeBlit inventory entry must retain its final CodecHash");

        var changed = RunContractGenerator(ContractSource("long"), baselineResult.Json);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing the physical UnsafeBlit layout must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedUnsafeBlitLayoutChangeShouldFailContractBaseline()
    {
        static string ContractSource(string fieldType) => BuildSource($$"""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RawPayload
{
    public {{fieldType}} Value;
}

[SharpLink.Sdk.RpcContract]
public interface IRawService : SharpLink.Sdk.IService
{
    ValueTask<List<RawPayload>> Echo(List<RawPayload> value, CancellationToken cancellationToken);
}
""");

        var baselineResult = RunContractGenerator(ContractSource("int"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(baselineResult.Json)!.AsObject();
        var rawCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "RawPayload");
        Ensure(rawCodec["kind"]!.GetValue<string>() == "Final",
            "nested UnsafeBlit leaf must be retained in the complete final codec inventory");
        Ensure(IsValidCodecHashText(rawCodec["codecHash"]?.GetValue<string>()),
            "nested UnsafeBlit leaf must retain its final CodecHash");

        var changed = RunContractGenerator(ContractSource("long"), baselineResult.Json);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing a nested UnsafeBlit leaf inside a collection must fail baseline comparison");
        return Task.CompletedTask;
    }
}
