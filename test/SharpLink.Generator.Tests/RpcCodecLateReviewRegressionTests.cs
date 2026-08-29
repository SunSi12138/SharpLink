using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task TupleElementNamesShouldNotAffectRouteIdsOrWireLookup()
    {
        static string Source(string directTuple, string nestedTuple) => AddAssemblyAttributes(BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface ITupleIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<{{directTuple}}> Direct({{directTuple}} value, CancellationToken cancellationToken);
    ValueTask<List<{{nestedTuple}}>> Nested(List<{{nestedTuple}}> value, CancellationToken cancellationToken);
}

public sealed class TupleIdentityAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "tuple.identity/v1";
    public string WireFormatId => "tuple-identity-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(TupleIdentityAdapter), \"tuple.identity/v1\", \"tuple-identity-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ValueTuple<int, string>), typeof(TupleIdentityAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ValueTuple<int, int>), typeof(TupleIdentityAdapter))]");

        var baseline = RunContractGenerator(Source("(int X, string Y)", "(int Left, int Right)"));
        var renamed = RunContractGenerator(
            Source("(int Index, string Label)", "(int First, int Second)"),
            baseline.Json);

        Ensure(!renamed.Diagnostics.Any(IsCompatibilityDiagnostic),
            "renaming tuple elements must not change RPC method identity or contract compatibility");

        var root = System.Text.Json.Nodes.JsonNode.Parse(renamed.Json)!.AsObject();
        var direct = root["contracts"]!.AsArray()
            .SelectMany(static contract => contract!["methods"]!.AsArray())
            .Select(static method => method!.AsObject())
            .Single(static method => method["name"]!.GetValue<string>() == "Direct");
        Ensure(direct["request"]![0]!["wireFormatId"]!.GetValue<string>() == "tuple-identity-wire/v1",
            "named tuple request lookup must use the selected Adapter wire identity");
        Ensure(direct["response"]!["wireFormatId"]!.GetValue<string>() == "tuple-identity-wire/v1",
            "named tuple response lookup must use the selected Adapter wire identity");
        Ensure(renamed.Json.Contains("System.ValueTuple", StringComparison.Ordinal),
            "contract type keys must use the canonical CLR ValueTuple identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsafeBlitLayoutChangesShouldBreakCompatibility()
    {
        static string Source(string fields, bool list) => BuildSource($$"""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct RawPayload
{
    {{fields}}
}

[SharpLink.Sdk.RpcContract]
public interface IRawPayloadContract : SharpLink.Sdk.IService
{
    {{(list
        ? "ValueTask<List<RawPayload>> Echo(List<RawPayload> value, CancellationToken cancellationToken);"
        : "ValueTask<RawPayload> Echo(RawPayload value, CancellationToken cancellationToken);")}}
}
""");

        var baseline = RunContractGenerator(Source("public int A; public short B;", list: false)).Json;
        var addField = RunContractGenerator(Source("public int A; public short B; public byte C;", list: false), baseline);
        var changeType = RunContractGenerator(Source("public int A; public long B;", list: false), baseline);
        var changeOrder = RunContractGenerator(Source("public short B; public int A;", list: false), baseline);

        Ensure(addField.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "adding an unmanaged field changes UnsafeBlit raw wire layout");
        Ensure(changeType.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing an unmanaged field type changes UnsafeBlit raw wire layout");
        Ensure(changeOrder.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing unmanaged field order changes UnsafeBlit raw wire layout");

        var listBaseline = RunContractGenerator(Source("public int A; public short B;", list: true)).Json;
        var listChanged = RunContractGenerator(
            Source("public int A; public short B; public byte C;", list: true),
            listBaseline);
        Ensure(listChanged.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "native collection compatibility must include the nested UnsafeBlit element layout");
        return Task.CompletedTask;
    }

    [Test]
    public Task MetadataDtoSchemaChangesShouldBreakCompatibility()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        MetadataReference PayloadReference(string members) => CreateMetadataReference(
            "MetadataPayloads",
            $$"""
using SharpLink.Sdk;

namespace MetadataPayloads
{
    public sealed class Payload
    {
        {{members}}
    }

    public sealed class SdkReferenceMarker
    {
        public IService? Service { get; set; }
    }
}
""",
            sdk);

        const string source = """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface IMetadataPayloadContract : IService
{
    ValueTask<MetadataPayloads.Payload> Echo(MetadataPayloads.Payload value, CancellationToken cancellationToken);
}
""";

        var v1 = PayloadReference("public int Value { get; set; }");
        var v2 = PayloadReference("public int Value { get; set; } public string Name { get; set; } = string.Empty;");
        var baseline = RunContractGeneratorWithReferences(source, null, sdk, v1);
        var current = RunContractGeneratorWithReferences(source, baseline.Json, sdk, v2);

        var baselineRoot = System.Text.Json.Nodes.JsonNode.Parse(baseline.Json)!.AsObject();
        Ensure(!baselineRoot["dtos"]!.AsArray().Any(static item =>
                item!["name"]!.GetValue<string>() == "MetadataPayloads.Payload"),
            "metadata DTOs intentionally have no detailed source DTO descriptor");
        Ensure(baselineRoot["codecs"]!.AsArray().Any(static item =>
                item!["type"]!.GetValue<string>() == "MetadataPayloads.Payload" &&
                item["kind"]!.GetValue<string>() == "Dto"),
            "metadata DTO compatibility still publishes its generated Codec identity");
        Ensure(current.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "a metadata DTO SchemaId change must be compared when no detailed DTO descriptor exists");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkPrimitiveElementBindingShouldBeRejectedWithoutChangingCompositeDefaults()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IBuiltinCompositeContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int[]> EchoArray(int[] value, CancellationToken cancellationToken);
    ValueTask<List<int>> EchoList(List<int> value, CancellationToken cancellationToken);
    ValueTask<int?> EchoNullable(int? value, CancellationToken cancellationToken);
}

public sealed class CompositeIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "composite-int/v1";
    public string WireFormatId => "composite-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(CompositeIntAdapter), \"composite-int/v1\", \"composite-int-wire/v1\")]",
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
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StandaloneIntEnvelope
{
    public int Value { get; set; }
}

public sealed class OpaqueEnvelope
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecImplementation("opaque-envelope-wire/v1", "opaque-envelope-schema/v1")]
public sealed class OpaqueEnvelopeCodec : SharpLink.Abstractions.IRpcCodec<OpaqueEnvelope>
{
}

[SharpLink.Sdk.RpcContract]
public interface IOpaqueEnvelopeContract : SharpLink.Sdk.IService
{
    ValueTask<OpaqueEnvelope> Echo(OpaqueEnvelope value, CancellationToken cancellationToken);
}

public sealed class UnrelatedIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "unrelated-int/v1";
    public string WireFormatId => "unrelated-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(OpaqueEnvelope), typeof(OpaqueEnvelopeCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(UnrelatedIntAdapter), \"unrelated-int/v1\", \"unrelated-int-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(UnrelatedIntAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK049") == 1,
            "an int hidden below an opaque Contract Codec must not suppress the unrelated standalone builtin override diagnostic");

        var json = RunContractGenerator(source).Json;
        var codecTypes = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject()["codecs"]!.AsArray()
            .Select(static item => item!["type"]!.GetValue<string>())
            .ToArray();
        Ensure(codecTypes.Contains("OpaqueEnvelope", StringComparer.Ordinal),
            "the opaque final Contract Codec must remain in the compatibility graph");
        Ensure(!codecTypes.Any(static type => type is "int" or "System.Int32"),
            "CLR members below an opaque final Codec must not become phantom compatibility nodes");
        return Task.CompletedTask;
    }

    private static ContractGeneratorResult RunContractGeneratorWithReferences(
        string source,
        string? baseline,
        params MetadataReference[] references)
    {
        const string baselinePath = "/contracts/previous.sharplink.json";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            "ContractManifestTestAssembly",
            [syntaxTree],
            GetPlatformReferences().Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var additionalTexts = ImmutableArray<AdditionalText>.Empty;
        if (baseline is not null)
        {
            properties["build_property.SharpLinkContractBaseline"] = baselinePath;
            additionalTexts = [new InMemoryAdditionalText(baselinePath, baseline)];
        }

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts,
            CSharpParseOptions.Default,
            new TestAnalyzerConfigOptionsProvider(properties));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        var generated = result.GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .First(static text => text.Contains("__SharpLinkContractManifest", StringComparison.Ordinal));
        const string startMarker = "internal const string Json = @\"";
        const string endMarker = "\";";
        var start = generated.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var end = generated.LastIndexOf(endMarker, StringComparison.Ordinal);
        Ensure(start >= startMarker.Length && end > start, "generated contract Manifest constant");
        var json = generated.Substring(start, end - start).Replace("\"\"", "\"", StringComparison.Ordinal);
        return new ContractGeneratorResult(json, result.Diagnostics);
    }
}
