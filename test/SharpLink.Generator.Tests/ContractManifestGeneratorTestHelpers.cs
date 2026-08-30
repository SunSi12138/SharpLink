using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    private static string SimpleContract(string methods) => BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    {{methods}}
}
""");

    private static string DtoContract(string members) => BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    {{members}}
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");

    private static string AdapterContractSource(
        bool includeNativeEnvelope = false,
        ulong semanticLow = 0x2222222222222222UL)
    {
        var payloadType = includeNativeEnvelope ? "Envelope" : "Graph";
        var envelope = includeNativeEnvelope
            ? """
[SharpLink.Sdk.RpcSerializable]
public sealed class Envelope
{
    public Graph Graph { get; set; } = new();
}

"""
            : string.Empty;
        return AddAssemblyAttribute(BuildSource($$"""
[FakePackable]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

{{envelope}}[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<{{payloadType}}> Echo({{payloadType}} value);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1111111111111111UL, {{semanticLow}}UL)]
public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");
    }

    private static string AdapterStreamingContractSource()
        => AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcSerializable]
public sealed class Envelope
{
    public Graph Graph { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
    ValueTask<int> Upload(IAsyncEnumerable<Graph> values);
    IAsyncEnumerable<Graph> Watch(int count);
    ValueTask<Envelope> Wrap(Envelope value);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1111111111111111UL, 0x2222222222222222UL)]
public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

    private static string RewriteManifest(
        string json,
        Action<System.Text.Json.Nodes.JsonObject> rewrite)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        rewrite(root);
        root["schemaFingerprint"] = string.Empty;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var canonical = root.ToJsonString(options);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        root["schemaFingerprint"] = Convert.ToHexStringLower(fingerprint);
        return root.ToJsonString(options) + "\n";
    }

    private static string RemoveTopLevelProperty(string json, string propertyName)
        => RewriteManifest(json, root => root.Remove(propertyName));

    private static string SetTopLevelPropertyToNull(string json, string propertyName)
        => RewriteManifest(json, root => root[propertyName] = null);

    private static string RemoveCodecHashForType(string json, string typeName)
        => RewriteManifest(json, root => RemoveCodecHashForType(root, typeName));

    private static void RemoveCodecHashForType(System.Text.Json.Nodes.JsonNode node, string typeName)
    {
        if (node is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            if (jsonObject["type"]?.GetValue<string>() == typeName)
                jsonObject.Remove("codecHash");
            foreach (var child in jsonObject.Select(static property => property.Value)
                         .OfType<System.Text.Json.Nodes.JsonNode>().ToArray())
            {
                RemoveCodecHashForType(child, typeName);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<System.Text.Json.Nodes.JsonNode>())
                RemoveCodecHashForType(child, typeName);
        }
    }

    private static string RemoveDtoMemberCodecHash(
        string json,
        string dtoName,
        string memberName)
        => RewriteManifest(json, root =>
        {
            var dto = root["dtos"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(item => item["name"]!.GetValue<string>() == dtoName);
            var member = dto["members"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(item => item["name"]!.GetValue<string>() == memberName);
            member.Remove("codecHash");
        });

    private static string SetCodecInventoryHash(string json, string typeName, string? replacement)
        => RewriteManifest(json, root =>
        {
            var codec = root["codecs"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(item => item["type"]!.GetValue<string>() == typeName);
            codec["codecHash"] = replacement;
        });

    private static IEnumerable<System.Text.Json.Nodes.JsonObject> EnumerateJsonObjects(
        System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            yield return jsonObject;
            foreach (var child in jsonObject.Select(static property => property.Value)
                         .OfType<System.Text.Json.Nodes.JsonNode>())
            {
                foreach (var nested in EnumerateJsonObjects(child))
                    yield return nested;
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<System.Text.Json.Nodes.JsonNode>())
            {
                foreach (var nested in EnumerateJsonObjects(child))
                    yield return nested;
            }
        }
    }

    private static bool IsValidCodecHashText(string? value)
        => value is { Length: 32 } && value.All(static character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));

    private static void EnsurePayloadIdentity(
        System.Text.Json.Nodes.JsonNode node,
        bool expectOpaqueCodecHash,
        bool? stream,
        string scenario)
    {
        var value = node.AsObject();
        Ensure(!string.IsNullOrWhiteSpace(value["wireType"]?.GetValue<string>()),
            $"{scenario} wire type");
        Ensure(!value.ContainsKey("wireFormatId"),
            $"{scenario} must not contain legacy wireFormatId");
        if (expectOpaqueCodecHash)
        {
            Ensure(IsValidCodecHashText(value["codecHash"]?.GetValue<string>()),
                $"{scenario} opaque CodecHash");
        }
        else
        {
            Ensure(!value.ContainsKey("codecHash"),
                $"{scenario} native payload position does not need a second identity field");
        }
        if (stream is not null)
            Ensure(value["stream"]?.GetValue<bool>() == stream, $"{scenario} stream shape");
    }

    private static bool IsCompatibilityDiagnostic(Diagnostic diagnostic)
        => string.CompareOrdinal(diagnostic.Id, "SHARPLINK024") >= 0 &&
           string.CompareOrdinal(diagnostic.Id, "SHARPLINK035") <= 0;

    private static ContractGeneratorResult RunContractGenerator(
        string source,
        string? baseline = null,
        string? outputPath = null)
    {
        const string baselinePath = "/contracts/previous.sharplink.json";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            "ContractManifestTestAssembly",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var additionalTexts = ImmutableArray<AdditionalText>.Empty;
        if (baseline is not null)
        {
            properties["build_property.SharpLinkContractBaseline"] = baselinePath;
            additionalTexts = [new InMemoryAdditionalText(baselinePath, baseline)];
        }
        if (outputPath is not null)
            properties["build_property.SharpLinkContractManifestOutput"] = outputPath;

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

    private sealed record ContractGeneratorResult(string Json, ImmutableArray<Diagnostic> Diagnostics);

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content);
    }

    private sealed class TestAnalyzerConfigOptionsProvider(
        IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new TestAnalyzerConfigOptions(properties);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        internal static TestAnalyzerConfigOptions Empty { get; } = new(new Dictionary<string, string>());
        public override bool TryGetValue(string key, out string value)
            => values.TryGetValue(key, out value!);
    }
}
