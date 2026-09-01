using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ContractManifestShouldBeDeterministicAndContainTheWireSchema()
    {
        var source = BuildSource("""
public enum Status : byte { Unknown, Ready }

[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    [SharpLink.Sdk.RpcMember(7)]
    public required string Name { get; set; }
    public Status Status { get; set; }
}

[SharpLink.Sdk.RpcUnionCase(1, typeof(Payload))]
public interface IResultUnion { }

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
    IAsyncEnumerable<Status> Watch(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken) => new(value);
    public async IAsyncEnumerable<Status> Watch(CancellationToken cancellationToken) { yield break; }
}
""");

        var first = RunContractGenerator(source);
        var second = RunContractGenerator(source);
        Ensure(first.Json == second.Json, "identical source must emit byte-identical contract JSON");
        Ensure(first.Json.Contains("\"format\": \"SharpLink.Contracts\"", StringComparison.Ordinal),
            "Manifest format marker");
        Ensure(first.Json.Contains("\"shape\": \"Unary\"", StringComparison.Ordinal),
            "RPC call shape");
        Ensure(first.Json.Contains("\"wireType\": \"LengthDelimited\"", StringComparison.Ordinal),
            "DTO wire type");
        Ensure(!first.Json.Contains("wireFormatId", StringComparison.Ordinal),
            "legacy wire-format identity must not be emitted");
        Ensure(first.Json.Contains("\"codecHash\":", StringComparison.Ordinal),
            "reachable Codec inventory must contain deterministic identities");
        Ensure(first.Json.Contains("\"required\": true", StringComparison.Ordinal),
            "required DTO member");
        Ensure(first.Json.Contains("\"underlyingType\": \"byte\"", StringComparison.Ordinal),
            "enum underlying type");
        Ensure(first.Json.Contains("\"tag\": 1", StringComparison.Ordinal), "union tag");
        Ensure(first.Json.Contains("\"schemaFingerprint\":", StringComparison.Ordinal),
            "schema fingerprint");
        var generatorVersion = typeof(RpcGenerator).Assembly.GetName().Version!.ToString(3);
        Ensure(first.Json.Contains($"\"generatorVersion\": \"{generatorVersion}\";", StringComparison.Ordinal),
            "executing generator assembly version");
        Ensure(!first.Json.Contains(Directory.GetCurrentDirectory(), StringComparison.Ordinal),
            "Manifest must not contain absolute paths");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedAssemblyManifestShouldReportExecutingGeneratorVersion()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        var generatorVersion = typeof(RpcGenerator).Assembly.GetName().Version!.ToString(3);
        Ensure(generated.Contains($"public string GeneratorVersion => \"{generatorVersion}\";", StringComparison.Ordinal),
            "generated assembly Manifest version");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomCodecHashShouldBeRecordedInContractManifest()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1111111111111111UL, 0x2222222222222222UL)]
public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        var json = RunContractGenerator(source).Json;
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var moneyCodec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "Money");

        Ensure(moneyCodec["kind"]!.GetValue<string>() == "Custom", "custom Codec kind");
        Ensure(IsValidCodecHashText(moneyCodec["codecHash"]?.GetValue<string>()),
            "custom Codec must record a fixed-width CodecHash");
        Ensure(!moneyCodec.ContainsKey("wireFormatId") && !moneyCodec.ContainsKey("schemaId"),
            "custom Codec inventory must not restore legacy string identities");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomCodecSemanticIdentityChangeShouldBeDetectedForDirectPayloads()
    {
        string ContractSource(ulong semanticLow) => BuildSource($$"""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1111111111111111UL, {{semanticLow}}UL)]
public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(ContractSource(0x2222222222222222UL)).Json;
        var changed = RunContractGenerator(ContractSource(0x3333333333333333UL), baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing an opaque custom Codec semantic identity must fail baseline comparison");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidTimeoutConstantsShouldReportSharplink050()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ITimeoutContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(0)]
    ValueTask<int> Zero(CancellationToken cancellationToken);

    [SharpLink.Sdk.Timeout(-1)]
    ValueTask<int> Negative(CancellationToken cancellationToken);

    [SharpLink.Sdk.Timeout(double.NaN)]
    ValueTask<int> NotANumber(CancellationToken cancellationToken);

    [SharpLink.Sdk.Timeout(double.PositiveInfinity)]
    ValueTask<int> Infinity(CancellationToken cancellationToken);

    [SharpLink.Sdk.Timeout(double.Epsilon)]
    ValueTask<int> RoundsToZero(CancellationToken cancellationToken);

    [SharpLink.Sdk.Timeout(1e300)]
    ValueTask<int> Overflow(CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK050", 6);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("ITimeoutContract", StringComparison.Ordinal),
            "a contract with an invalid timeout must not emit descriptors");

        var valid = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IValidTimeoutContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1.5)]
    ValueTask<int> Invoke(CancellationToken cancellationToken);
}
""");
        EnsureDoesNotHaveRule(valid, "SHARPLINK050");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(valid)).Contains(
                "TimeSpan.FromTicks(15000000L)",
                StringComparison.Ordinal),
            "a valid fractional timeout must retain its generated descriptor");
        return Task.CompletedTask;
    }

    [Test]
    public Task NonPositiveUnionTagsShouldReportSharplink051()
    {
        var source = BuildSource("""
public sealed class ValidCase : IResultUnion { }

[SharpLink.Sdk.RpcUnionCase(0, typeof(ValidCase))]
[SharpLink.Sdk.RpcUnionCase(-1, typeof(ValidCase))]
public interface IResultUnion { }
""");

        EnsureRuleCount(source, "SHARPLINK051", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidUnionCaseMappingsShouldReportSharplink051()
    {
        var source = BuildSource("""
public abstract class AbstractCase : ResultBase { }
public interface InterfaceCase : IResultUnion { }
public sealed class UnrelatedCase { }
public sealed class OpenCase<T> : ResultBase { }
public sealed class ValidCase : ResultBase { }

[SharpLink.Sdk.RpcUnionCase(1, typeof(AbstractCase))]
[SharpLink.Sdk.RpcUnionCase(6, typeof(InterfaceCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(UnrelatedCase))]
[SharpLink.Sdk.RpcUnionCase(3, typeof(OpenCase<>))]
[SharpLink.Sdk.RpcUnionCase(4, typeof(ValidCase))]
[SharpLink.Sdk.RpcUnionCase(5, typeof(ValidCase))]
public abstract class ResultBase { }
""");

        EnsureRuleCount(source, "SHARPLINK051", 5);
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedManifestCollectionsShouldNotExposeMutableArrays()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IImmutableManifestService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class ImmutableManifestService : IImmutableManifestService
{
    public ImmutableManifestService(object dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}
""");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));

        Ensure(generated.Contains("Array.AsReadOnly(__contracts)", StringComparison.Ordinal),
            "contract descriptors must be exposed through a read-only wrapper");
        Ensure(generated.Contains("Array.AsReadOnly(__services)", StringComparison.Ordinal),
            "service descriptors must be exposed through a read-only wrapper");
        Ensure(generated.Contains("Array.AsReadOnly(__codecs)", StringComparison.Ordinal),
            "Codec factories must be exposed through a read-only wrapper");
        Ensure(generated.Contains("Array.AsReadOnly(__dependencies)", StringComparison.Ordinal),
            "dependency identities must be exposed through a read-only wrapper");
        Ensure(generated.Contains("Array.AsReadOnly(new SharpLinkGeneratedMethodDescriptor[]", StringComparison.Ordinal),
            "nested method descriptors must not expose their generated array");
        Ensure(generated.Contains("Array.AsReadOnly(new Type[]", StringComparison.Ordinal),
            "nested service dependencies must not expose their generated array");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutAdapterCodecHashShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = RemoveCodecHashForType(RunContractGenerator(source).Json, "Graph");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing an opaque Adapter CodecHash is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutDtoMemberCodecHashShouldBeRejected()
    {
        var source = AdapterContractSource(includeNativeEnvelope: true);
        var baseline = RemoveDtoMemberCodecHash(RunContractGenerator(source).Json, "Envelope", "Graph");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing an opaque DTO-member CodecHash is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutReachableCodecIdentityInventoryShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = RemoveTopLevelProperty(RunContractGenerator(source).Json, "codecs");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing the reachable Codec identity inventory is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithNullReachableCodecIdentityInventoryShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = SetTopLevelPropertyToNull(RunContractGenerator(source).Json, "codecs");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a null reachable Codec identity inventory is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitAdapterSemanticIdentityChangeShouldBeRejected()
    {
        var baseline = RunContractGenerator(AdapterContractSource()).Json;
        var changed = RunContractGenerator(
            AdapterContractSource(semanticLow: 0x3333333333333333UL),
            baseline);

        Ensure(!changed.Diagnostics.Any(IsCompatibilityDiagnostic) ||
               changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "an opaque Adapter semantic identity change is incompatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterSemanticIdentityChangeInsideNativeCollectionShouldBeRejected()
    {
        var baselineSource = AdapterContractSource().Replace(
            "ValueTask<Graph> Echo(Graph value);",
            "ValueTask<List<Graph>> Echo(List<Graph> value);",
            StringComparison.Ordinal);
        var baseline = RunContractGenerator(baselineSource).Json;
        var baselineDocument = System.Text.Json.Nodes.JsonNode.Parse(baseline)!.AsObject();
        var nestedCodec = baselineDocument["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "Graph");
        Ensure(IsValidCodecHashText(nestedCodec["codecHash"]?.GetValue<string>()),
            "the Manifest records the nested collection element CodecHash");
        var changedSource = AdapterContractSource(semanticLow: 0x3333333333333333UL).Replace(
            "ValueTask<Graph> Echo(Graph value);",
            "ValueTask<List<Graph>> Echo(List<Graph> value);",
            StringComparison.Ordinal);

        var changed = RunContractGenerator(changedSource, baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "a nested Adapter semantic identity change inside a native collection is incompatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task NativePayloadManifestShouldNotContainLegacyWireIdentity()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class Graph
{
    public string Name { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}
""");
        var current = RunContractGenerator(source);
        var root = System.Text.Json.Nodes.JsonNode.Parse(current.Json)!.AsObject();
        Ensure(!current.Json.Contains("wireFormatId", StringComparison.Ordinal),
            "native Manifest must not restore legacy wireFormatId");
        var method = root["contracts"]!.AsArray().Single()!["methods"]!.AsArray().Single()!.AsObject();
        EnsurePayloadIdentity(method["request"]!.AsArray()[0]!, false, false, "native request");
        EnsurePayloadIdentity(method["response"]!, false, false, "native response");
        var codec = root["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "Graph");
        Ensure(IsValidCodecHashText(codec["codecHash"]?.GetValue<string>()),
            "native Codec inventory still publishes deterministic CodecHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManifestShouldRecordStructuralWireTypesAndOpaqueCodecHashes()
    {
        var current = RunContractGenerator(AdapterStreamingContractSource());
        var root = System.Text.Json.Nodes.JsonNode.Parse(current.Json)!.AsObject();
        var wireEntries = EnumerateJsonObjects(root)
            .Where(static item => item.ContainsKey("wireType"))
            .ToArray();
        Ensure(wireEntries.Length == 9, "eight method payload positions and one DTO member");
        Ensure(wireEntries.All(static item =>
                !string.IsNullOrWhiteSpace(item["wireType"]?.GetValue<string>())),
            "every serialized Manifest position has a structural wireType");
        Ensure(wireEntries.All(static item => !item.ContainsKey("wireFormatId")),
            "serialized Manifest positions must not contain legacy wireFormatId");

        var contract = root["contracts"]!.AsArray().Single()!.AsObject();
        var methods = contract["methods"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToDictionary(
                static item => item["name"]!.GetValue<string>(),
                StringComparer.Ordinal);
        var echo = methods["Echo"];
        EnsurePayloadIdentity(echo["request"]!.AsArray()[0]!, true, false, "unary request");
        EnsurePayloadIdentity(echo["response"]!, true, false, "unary response");

        var upload = methods["Upload"];
        EnsurePayloadIdentity(upload["request"]!.AsArray()[0]!, true, true, "request stream item");
        EnsurePayloadIdentity(upload["response"]!, false, false, "upload response");

        var watch = methods["Watch"];
        EnsurePayloadIdentity(watch["request"]!.AsArray()[0]!, false, false, "watch request");
        EnsurePayloadIdentity(watch["response"]!, true, true, "response stream item");

        var wrap = methods["Wrap"];
        EnsurePayloadIdentity(wrap["request"]!.AsArray()[0]!, false, false, "native envelope request");
        EnsurePayloadIdentity(wrap["response"]!, false, false, "native envelope response");

        var envelope = root["dtos"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "Envelope");
        var graphMember = envelope["members"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "Graph");
        EnsurePayloadIdentity(graphMember, true, stream: null, "nested DTO member");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidCodecHashesShouldInvalidateBaseline()
    {
        var source = AdapterContractSource();
        var valid = RunContractGenerator(source).Json;
        var invalidBaselines = new[]
        {
            SetCodecInventoryHash(valid, "Graph", replacement: null),
            SetCodecInventoryHash(valid, "Graph", string.Empty),
            SetCodecInventoryHash(valid, "Graph", " "),
            SetCodecInventoryHash(valid, "Graph", "abc"),
            SetCodecInventoryHash(valid, "Graph", new string('g', 32))
        };

        foreach (var baseline in invalidBaselines)
        {
            var compared = RunContractGenerator(source, baseline);
            Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
                $"missing or malformed fixed-width CodecHash invalidates the baseline. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterImplementationAndIdChangeWithStableSemanticIdentityShouldRemainCompatible()
    {
        var baselineSource = AdapterContractSource();
        var baseline = RunContractGenerator(baselineSource).Json;
        var changedSource = baselineSource
            .Replace("FakeAdapter", "ReplacementAdapter", StringComparison.Ordinal)
            .Replace("fake.adapter/v1", "replacement.adapter/v2", StringComparison.Ordinal);

        var changed = RunContractGenerator(changedSource, baseline);

        Ensure(!changed.Diagnostics.Any(IsCompatibilityDiagnostic),
            $"Adapter implementation/lifecycle identity changes do not change wire semantics when the explicit semantic identity is stable. Diagnostics: {FormatDiagnostics(changed.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullableRequestResponseAndStreamItemsShouldBeRecorded()
    {
        var source = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface INullableService : SharpLink.Sdk.IService
{
    ValueTask<string?> Maybe(string? value, CancellationToken cancellationToken);
    ValueTask<int> Upload(IAsyncEnumerable<string?> values, CancellationToken cancellationToken);
    IAsyncEnumerable<string?> Watch(CancellationToken cancellationToken);
}
""");

        var current = RunContractGenerator(source);
        var nullableValues = current.Json.Split(
            "\"nullable\": true",
            StringSplitOptions.None).Length - 1;
        Ensure(nullableValues == 4, "nullable request, response, request stream item, and response stream item");

        var changed = RunContractGenerator(
            source.Replace("string?", "string", StringComparison.Ordinal),
            current.Json);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "payload nullability compatibility diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task NoBaselineAndValidBaselineShouldNotReportCompatibilityErrors()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var current = RunContractGenerator(source);
        Ensure(!current.Diagnostics.Any(IsCompatibilityDiagnostic), "no baseline only emits current Manifest");

        var compared = RunContractGenerator(source, current.Json);
        Ensure(!compared.Diagnostics.Any(IsCompatibilityDiagnostic),
            "an identical valid baseline is compatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAndUnsupportedBaselinesShouldReportStableDiagnostics()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var invalid = RunContractGenerator(source, "{");
        Ensure(invalid.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK024"),
            "damaged baseline diagnostic");

        var baseline = RunContractGenerator(source).Json.Replace(
            "\"version\": 3", "\"version\": 99", StringComparison.Ordinal);
        var unsupported = RunContractGenerator(source, baseline);
        Ensure(unsupported.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK025"),
            "unsupported baseline version diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractAndMethodIdentityChangesShouldBeRejected()
    {
        var baselineSource = SimpleContract(
            "ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var baseline = RunContractGenerator(baselineSource).Json;

        var renamedContract = RunContractGenerator(
            baselineSource.Replace("IHelloService", "IRenamedService", StringComparison.Ordinal), baseline);
        Ensure(renamedContract.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK026"),
            "contract ID change diagnostic");

        var changedMethod = RunContractGenerator(SimpleContract(
            "ValueTask<int> Echo(long value, CancellationToken cancellationToken);"), baseline);
        Ensure(changedMethod.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK027"),
            "method ID change diagnostic");
        Ensure(changedMethod.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "request wire type change diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task CallShapeWireTypeAndMethodRemovalShouldBeRejected()
    {
        var baselineSource = SimpleContract("""
ValueTask<int> Echo(int value, CancellationToken cancellationToken);
ValueTask<int> Legacy(int value, CancellationToken cancellationToken);
""");
        var baseline = RunContractGenerator(baselineSource).Json;

        var changed = RunContractGenerator(SimpleContract("""
IAsyncEnumerable<long> Echo(int value, CancellationToken cancellationToken);
"""), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK029"),
            "call shape diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "response or stream item wire type diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK034"),
            "method removal diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractAndServiceRouteRemovalShouldBeRejected()
    {
        var contractSource = SimpleContract(
            "ValueTask<int> Echo(int value, CancellationToken cancellationToken);");
        var contractBaseline = RunContractGenerator(contractSource).Json;
        var removedContract = RunContractGenerator(BuildSource("public sealed class Implementation { }"), contractBaseline);
        Ensure(removedContract.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK035"),
            "contract removal diagnostic");

        var serviceSource = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}
""");
        var serviceBaseline = RunContractGenerator(serviceSource).Json;
        var removedService = RunContractGenerator(contractSource, serviceBaseline);
        Ensure(removedService.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK037"),
            "service route removal diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task RequiredMemberChangesAndDefaultIdRenameShouldBeRejected()
    {
        var baselineSource = DtoContract("""
[SharpLink.Sdk.RpcRequired, SharpLink.Sdk.RpcMember(1)]
public string Name { get; set; } = string.Empty;
public int Count { get; set; }
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var changed = RunContractGenerator(DtoContract("""
public int Total { get; set; }
[SharpLink.Sdk.RpcRequired, SharpLink.Sdk.RpcMember(2)]
public string Code { get; set; } = string.Empty;
"""), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK028"),
            "default member ID rename diagnostic");
        Ensure(changed.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK031") >= 2,
            "required member removal and addition diagnostics");
        return Task.CompletedTask;
    }

    [Test]
    public Task CompatibleOptionalFieldAndExplicitIdRenameShouldBeAllowed()
    {
        var baselineSource = DtoContract("""
[SharpLink.Sdk.RpcMember(7)]
public string Name { get; set; } = string.Empty;
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var compatible = RunContractGenerator(DtoContract("""
[SharpLink.Sdk.RpcMember(7)]
public string DisplayName { get; set; } = string.Empty;
[SharpLink.Sdk.RpcMember(8)]
public int OptionalCount { get; set; }
"""), baseline);
        Ensure(!compatible.Diagnostics.Any(IsCompatibilityDiagnostic),
            "legacy Contract Manifest structural baseline rules remain independent from #396 exact RpcAssemblyHash identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task EnumAndUnionTagChangesShouldBeRejected()
    {
        var baselineSource = BuildSource("""
public enum Status : byte { None, Ready }
public sealed class FirstCase { }
public sealed class SecondCase { }
[SharpLink.Sdk.RpcUnionCase(1, typeof(FirstCase))]
public interface IResultUnion { }
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Status> Echo(Status value, CancellationToken cancellationToken);
}
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var currentSource = baselineSource
            .Replace("Status : byte", "Status : int", StringComparison.Ordinal)
            .Replace("RpcUnionCase(1, typeof(FirstCase))", "RpcUnionCase(1, typeof(SecondCase))", StringComparison.Ordinal);
        var changed = RunContractGenerator(currentSource, baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK032"),
            "enum underlying type diagnostic");
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK033"),
            "union tag reuse diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedCollectionEnumUnderlyingTypeChangesShouldBeRejected()
    {
        var baselineSource = BuildSource("""
public enum NestedStatus : byte { None, Ready }

[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    public List<NestedStatus> Values { get; set; } = [];
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""");
        var baseline = RunContractGenerator(baselineSource).Json;
        var changed = RunContractGenerator(
            baselineSource.Replace("NestedStatus : byte", "NestedStatus : int", StringComparison.Ordinal),
            baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK032"),
            "nested collection enum underlying type diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConfiguredManifestOutputShouldWriteTheExactJsonArtifact()
    {
        var output = Path.Combine(Path.GetTempPath(), $"sharplink-{Guid.NewGuid():N}.json");
        try
        {
            var result = RunContractGenerator(
                SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);"),
                outputPath: output);
            Ensure(File.Exists(output), "configured Manifest artifact exists");
            Ensure(File.ReadAllText(output) == result.Json, "artifact exactly matches generated JSON");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task UnrelatedImplementationChangesShouldReuseContractAnalysis()
    {
        var parseOptions = CSharpParseOptions.Default;
        var contractTree = CSharpSyntaxTree.ParseText(
            SimpleContract("ValueTask<int> Echo(int value, CancellationToken cancellationToken);"),
            parseOptions,
            path: "/contracts/Contract.cs");
        var implementationTree = CSharpSyntaxTree.ParseText(
            "public static class Implementation { public static int Value => 1; }",
            parseOptions,
            path: "/implementation/Implementation.cs");
        var compilation = CSharpCompilation.Create(
            "ContractIncrementalTestAssembly",
            [contractTree, implementationTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            ImmutableArray<AdditionalText>.Empty,
            parseOptions,
            new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>()),
            new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var changedImplementationTree = CSharpSyntaxTree.ParseText(
            "public static class Implementation { public static int Value => 2; }",
            parseOptions,
            path: "/implementation/Implementation.cs");
        compilation = compilation.ReplaceSyntaxTree(implementationTree, changedImplementationTree);
        driver = driver.RunGenerators(compilation);

        var steps = driver.GetRunResult().Results.Single().TrackedSteps["SharpLink.ContractManifestAnalysis"];
        Ensure(steps.Length > 0, "contract Manifest analysis tracking step");
        Ensure(
            steps.SelectMany(static step => step.Outputs).All(static output =>
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "unrelated implementation edits must not rerun contract Manifest analysis");
        return Task.CompletedTask;
    }
}
