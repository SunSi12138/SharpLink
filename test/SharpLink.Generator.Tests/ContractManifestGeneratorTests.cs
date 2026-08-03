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
        Ensure(first.Json.Contains("\"wireFormatId\": \"sharplink-native/v1\"", StringComparison.Ordinal),
            "native wire-format identity");
        Ensure(first.Json.Contains("\"required\": true", StringComparison.Ordinal),
            "required DTO member");
        Ensure(first.Json.Contains("\"underlyingType\": \"byte\"", StringComparison.Ordinal),
            "enum underlying type");
        Ensure(first.Json.Contains("\"tag\": 1", StringComparison.Ordinal), "union tag");
        Ensure(first.Json.Contains("\"schemaFingerprint\":", StringComparison.Ordinal),
            "schema fingerprint");
        var generatorVersion = typeof(RpcGenerator).Assembly.GetName().Version!.ToString(3);
        Ensure(first.Json.Contains($"\"generatorVersion\": \"{generatorVersion}\"", StringComparison.Ordinal),
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
        Ensure(!generated.Contains("ITimeoutContract_Proxy", StringComparison.Ordinal),
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
                "TimeSpan.FromSeconds(1.5d)",
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
    public Task BaselineWithoutAdapterWireFormatShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = RemoveWireFormat(RunContractGenerator(source).Json, "fake-wire/v1");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing adapter wireFormatId is invalid. Baseline: {baseline} Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutDtoMemberWireFormatShouldBeRejected()
    {
        var source = AdapterContractSource(includeNativeEnvelope: true);
        var baseline = RemoveWireFormat(RunContractGenerator(source).Json, "fake-wire/v1");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing a DTO member wireFormatId is invalid. Baseline: {baseline} Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutReachableCodecWireInventoryShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = RemoveTopLevelProperty(RunContractGenerator(source).Json, "codecs");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing the reachable Codec wire inventory is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithNullReachableCodecWireInventoryShouldBeRejected()
    {
        var source = AdapterContractSource();
        var baseline = SetTopLevelPropertyToNull(RunContractGenerator(source).Json, "codecs");

        var compared = RunContractGenerator(source, baseline);

        Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a null reachable Codec wire inventory is invalid. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitWireFormatChangeShouldBeRejected()
    {
        var baselineSource = AdapterContractSource();
        var baseline = RunContractGenerator(baselineSource).Json;
        var changedSource = baselineSource.Replace("fake-wire/v1", "other-wire/v1", StringComparison.Ordinal);

        var changed = RunContractGenerator(changedSource, baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "an explicit wire-format identity change is incompatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterWireFormatChangeInsideNativeCollectionShouldBeRejected()
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
        Ensure(nestedCodec["wireFormatId"]!.GetValue<string>() == "fake-wire/v1",
            "the Manifest records the nested collection element Codec wire identity");
        var changedSource = baselineSource.Replace(
            "fake-wire/v1",
            "other-wire/v1",
            StringComparison.Ordinal);

        var changed = RunContractGenerator(changedSource, baseline);

        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "a nested Adapter wire-format change inside a native collection is incompatible");
        return Task.CompletedTask;
    }

    [Test]
    public Task BaselineWithoutNativeWireFormatShouldBeRejected()
    {
        var baselineSource = BuildSource("""
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
        var invalidBaseline = RemoveWireFormat(
            RunContractGenerator(baselineSource).Json,
            "sharplink-native/v1");
        var currentSource = AdapterContractSource();

        var changed = RunContractGenerator(currentSource, invalidBaseline);

        Ensure(changed.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
            $"a baseline missing native wireFormatId is invalid. Baseline: {invalidBaseline} Diagnostics: {FormatDiagnostics(changed.Diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManifestShouldRecordRequiredWireFormatsAtEveryPayloadPosition()
    {
        var current = RunContractGenerator(AdapterStreamingContractSource());
        var root = System.Text.Json.Nodes.JsonNode.Parse(current.Json)!.AsObject();
        var wireEntries = EnumerateJsonObjects(root)
            .Where(static item => item.ContainsKey("wireType"))
            .ToArray();
        Ensure(wireEntries.Length == 9, "eight method payload positions and one DTO member");
        Ensure(wireEntries.All(static item =>
                !string.IsNullOrWhiteSpace(item["wireFormatId"]?.GetValue<string>())),
            "every serialized Manifest position has a required non-empty wireFormatId");

        var contract = root["contracts"]!.AsArray().Single()!.AsObject();
        var methods = contract["methods"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToDictionary(
                static item => item["name"]!.GetValue<string>(),
                StringComparer.Ordinal);
        var echo = methods["Echo"];
        EnsureWireFormat(echo["request"]!.AsArray()[0]!, "fake-wire/v1", stream: false, "unary request");
        EnsureWireFormat(echo["response"]!, "fake-wire/v1", stream: false, "unary response");

        var upload = methods["Upload"];
        EnsureWireFormat(upload["request"]!.AsArray()[0]!, "fake-wire/v1", stream: true, "request stream item");
        EnsureWireFormat(upload["response"]!, "sharplink-native/v1", stream: false, "upload response");

        var watch = methods["Watch"];
        EnsureWireFormat(watch["request"]!.AsArray()[0]!, "sharplink-native/v1", stream: false, "watch request");
        EnsureWireFormat(watch["response"]!, "fake-wire/v1", stream: true, "response stream item");

        var wrap = methods["Wrap"];
        EnsureWireFormat(wrap["request"]!.AsArray()[0]!, "sharplink-native/v1", stream: false, "native envelope request");
        EnsureWireFormat(wrap["response"]!, "sharplink-native/v1", stream: false, "native envelope response");

        var envelope = root["dtos"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "Envelope");
        var graphMember = envelope["members"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "Graph");
        EnsureWireFormat(graphMember, "fake-wire/v1", stream: null, "nested DTO member");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullBlankOrWhitespaceWireFormatShouldInvalidateBaseline()
    {
        var source = AdapterContractSource();
        var valid = RunContractGenerator(source).Json;
        var invalidBaselines = new[]
        {
            SetWireFormat(valid, "fake-wire/v1", replacement: null),
            SetWireFormat(valid, "fake-wire/v1", string.Empty),
            SetWireFormat(valid, "fake-wire/v1", " ")
        };

        foreach (var baseline in invalidBaselines)
        {
            var compared = RunContractGenerator(source, baseline);
            Ensure(compared.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK024") == 1,
                $"null, blank, and whitespace wireFormatId values each invalidate the baseline. Baseline: {baseline} Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterIdentityChangeWithStableWireFormatShouldRemainCompatible()
    {
        var baselineSource = AdapterContractSource();
        var baseline = RunContractGenerator(baselineSource).Json;
        var changedSource = baselineSource
            .Replace("FakeAdapter", "ReplacementAdapter", StringComparison.Ordinal)
            .Replace("fake.adapter/v1", "replacement.adapter/v2", StringComparison.Ordinal);

        var changed = RunContractGenerator(changedSource, baseline);

        Ensure(!changed.Diagnostics.Any(IsCompatibilityDiagnostic),
            $"Adapter implementation and ID changes are compatible when wireFormatId is stable. Diagnostics: {FormatDiagnostics(changed.Diagnostics)}");
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
            "\"version\": 1", "\"version\": 99", StringComparison.Ordinal);
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
        var memberIdDiagnostic = changed.Diagnostics.Single(static diagnostic => diagnostic.Id == "SHARPLINK028");
        Ensure(memberIdDiagnostic.Properties.TryGetValue("SharpLink.PreviousMemberId", out var previousId) &&
               uint.TryParse(previousId, out var parsedPreviousId) && parsedPreviousId > 0,
            "SHARPLINK028 previous member ID property");
        Ensure(changed.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK031") >= 2,
            "required member removal and addition diagnostics");
        var sourceRequiredDiagnostic = changed.Diagnostics.Single(diagnostic =>
            diagnostic.Id == "SHARPLINK031" &&
            diagnostic.Properties.ContainsKey("SharpLink.FixKind"));
        Ensure(sourceRequiredDiagnostic.Properties.TryGetValue("SharpLink.FixKind", out var requiredFixKind) &&
               requiredFixKind == "RemoveRpcRequired",
            "source-located SHARPLINK031 stable FixKind property");
        var removedRequiredDiagnostic = changed.Diagnostics.Single(diagnostic =>
            diagnostic.Id == "SHARPLINK031" &&
            !diagnostic.Properties.ContainsKey("SharpLink.FixKind"));
        Ensure(!removedRequiredDiagnostic.Properties.ContainsKey("SharpLink.FixKind"),
            "removed required member must not advertise a source fix kind");
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
            "explicit member ID rename and optional addition are compatible");
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
        var enumDiagnostic = changed.Diagnostics.Single(static diagnostic => diagnostic.Id == "SHARPLINK032");
        Ensure(enumDiagnostic.Properties.TryGetValue("SharpLink.PreviousEnumUnderlyingType", out var previousType) &&
               previousType is "System.Byte" or "byte",
            "SHARPLINK032 previous enum underlying type property");
        var unionDiagnostic = changed.Diagnostics.Single(static diagnostic => diagnostic.Id == "SHARPLINK033");
        Ensure(unionDiagnostic.Properties.TryGetValue("SharpLink.PreviousUnionTag", out var previousTag) &&
               previousTag == "1",
            "SHARPLINK033 previous union tag property");
        Ensure(unionDiagnostic.Properties.TryGetValue("SharpLink.PreviousUnionType", out var previousUnionType) &&
               previousUnionType == "FirstCase",
            "SHARPLINK033 previous union type property");
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

    private static string AdapterContractSource(bool includeNativeEnvelope = false)
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

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");
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

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

    private static string RemoveWireFormat(string json, string wireFormatId)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        RemoveWireFormat(root, wireFormatId);
        root["schemaFingerprint"] = string.Empty;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var canonical = root.ToJsonString(options);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        root["schemaFingerprint"] = Convert.ToHexStringLower(fingerprint);
        return root.ToJsonString(options) + "\n";
    }

    private static string RemoveTopLevelProperty(string json, string propertyName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root.Remove(propertyName);
        root["schemaFingerprint"] = string.Empty;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var canonical = root.ToJsonString(options);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        root["schemaFingerprint"] = Convert.ToHexStringLower(fingerprint);
        return root.ToJsonString(options) + "\n";
    }

    private static string SetTopLevelPropertyToNull(string json, string propertyName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root[propertyName] = null;
        root["schemaFingerprint"] = string.Empty;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var canonical = root.ToJsonString(options);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        root["schemaFingerprint"] = Convert.ToHexStringLower(fingerprint);
        return root.ToJsonString(options) + "\n";
    }

    private static void RemoveWireFormat(System.Text.Json.Nodes.JsonNode node, string wireFormatId)
    {
        if (node is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            if (jsonObject["wireFormatId"]?.GetValue<string>() == wireFormatId)
                jsonObject.Remove("wireFormatId");
            foreach (var child in jsonObject.Select(static property => property.Value).OfType<System.Text.Json.Nodes.JsonNode>().ToArray())
                RemoveWireFormat(child, wireFormatId);
        }
        else if (node is System.Text.Json.Nodes.JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<System.Text.Json.Nodes.JsonNode>())
                RemoveWireFormat(child, wireFormatId);
        }
    }

    private static string SetWireFormat(string json, string wireFormatId, string? replacement)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        SetWireFormat(root, wireFormatId, replacement);
        root["schemaFingerprint"] = string.Empty;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var canonical = root.ToJsonString(options);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        root["schemaFingerprint"] = Convert.ToHexStringLower(fingerprint);
        return root.ToJsonString(options) + "\n";
    }

    private static void SetWireFormat(
        System.Text.Json.Nodes.JsonNode node,
        string wireFormatId,
        string? replacement)
    {
        if (node is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            if (jsonObject["wireFormatId"]?.GetValue<string>() == wireFormatId)
                jsonObject["wireFormatId"] = replacement;
            foreach (var child in jsonObject.Select(static property => property.Value).OfType<System.Text.Json.Nodes.JsonNode>().ToArray())
                SetWireFormat(child, wireFormatId, replacement);
        }
        else if (node is System.Text.Json.Nodes.JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<System.Text.Json.Nodes.JsonNode>())
                SetWireFormat(child, wireFormatId, replacement);
        }
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonObject> EnumerateJsonObjects(
        System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            yield return jsonObject;
            foreach (var child in jsonObject.Select(static property => property.Value).OfType<System.Text.Json.Nodes.JsonNode>())
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

    private static void EnsureWireFormat(
        System.Text.Json.Nodes.JsonNode node,
        string expectedWireFormatId,
        bool? stream,
        string scenario)
    {
        var value = node.AsObject();
        Ensure(value["wireFormatId"]?.GetValue<string>() == expectedWireFormatId,
            $"{scenario} wireFormatId");
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
