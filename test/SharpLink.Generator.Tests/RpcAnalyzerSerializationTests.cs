using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{

    [Test]
    public Task GeneratedServerStubShouldResolveCodecsOnlyDuringConstruction()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IServerStubCodecService : SharpLink.Sdk.IService
{
    ValueTask<string> EchoAsync(string value, CancellationToken cancellationToken);
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
}
""");

        var stub = RunGeneratorAndGetSources(source)
            .Single(static text => text.Contains("private sealed class __Stub_", StringComparison.Ordinal));
        var constructorStart = stub.IndexOf("internal __Stub_", StringComparison.Ordinal);
        var constructorEnd = stub.IndexOf(
            "public bool SupportsCancellation",
            constructorStart,
            StringComparison.Ordinal);
        Ensure(constructorStart > 0 && constructorEnd > constructorStart,
            "generated Stub must contain a bounded constructor");

        var constructor = stub[constructorStart..constructorEnd];
        var outsideConstructor = stub[constructorEnd..];
        Ensure(constructor.Contains("__parameterCodec_", StringComparison.Ordinal) &&
               constructor.Contains("__responseCodec_", StringComparison.Ordinal),
            "generated Stub constructor must declare request/response Codec fields");
        Ensure(CountOccurrences(constructor, "codecs.GetCodec<string>()") == 2,
            "generated Stub constructor must resolve both request and response string Codec fields");
        Ensure(CountOccurrences(constructor, "codecs.GetCodec<int>()") == 3,
            "generated Stub constructor must resolve inbound, outbound, and unary response int Codec fields");
        Ensure(!outsideConstructor.Contains("GetCodec<", StringComparison.Ordinal),
            "generated Stub dispatch must not perform per-call Codec lookup");
        return Task.CompletedTask;
    }

    [Test]
    public Task SemanticFixedRequestValuesShouldUseValidatedBuiltInCodecs()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IValidatedValueService : SharpLink.Sdk.IService
{
    ValueTask<int> Validate(
        bool enabled,
        decimal amount,
        DateOnly day,
        DateTime timestamp,
        DateTimeOffset offset,
        TimeOnly time,
        System.Text.Rune rune,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "marker_enabled is not (0 or 1)") == 2,
            "proxy and stub request decoders must reject non-canonical Boolean markers");
        Ensure(generated.Contains("value.enabled ? (byte)1 : (byte)0", StringComparison.Ordinal),
            "the request encoder must canonicalize Boolean values");
        foreach (var type in new[]
                 {
                     "decimal", "global::System.DateOnly", "global::System.DateTime",
                     "global::System.DateTimeOffset", "global::System.TimeOnly", "global::System.Text.Rune"
                 })
        {
            Ensure(generated.Contains($"codecs.GetCodec<{type}>()", StringComparison.Ordinal),
                $"request value {type} must use its validating built-in Codec");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task ReachableDtoShouldGenerateCodecAndManifest()
    {
        var source = BuildSource("""
public sealed record Address([property: SharpLink.Sdk.RpcMember(7)] string City);

public sealed class Person
{
    [SharpLink.Sdk.RpcRequired]
    public string Name { get; init; } = string.Empty;
    public int Age { get; init; }
    public Address Address { get; init; } = new Address(string.Empty);
    public List<string> Tags { get; init; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Person> Echo(Person value);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var codecs = generated.FirstOrDefault(static text => text.Contains("Missing required RPC member 'Name'"));
        if (codecs is null)
            throw new Exception("Expected generated DTO codec source.");
        var manifest = generated.FirstOrDefault(static text => text.Contains("__SharpLinkGeneratedAssemblyManifest"));
        if (manifest is null)
            throw new Exception("Expected generated assembly manifest source.");
        Ensure(codecs.Contains("IRpcCodec<global::Person>"), "Person codec");
        Ensure(codecs.Contains("IRpcCodec<global::Address>"), "nested record codec");
        Ensure(codecs.Contains("IRpcCodec<global::System.Collections.Generic.List<string>>"), "collection codec");
        Ensure(manifest.Contains("SharpLinkGeneratedAssemblyCatalog.Register"), "manifest registration");
        Ensure(manifest.Contains(".Factory()"), "codec factories belong to the assembly manifest");
        Ensure(codecs.Contains("case 7U:"), "explicit field ID");
        Ensure(codecs.Contains("Missing required RPC member 'Name'"), "required member validation");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectStringDtosShouldCacheExactUtf16SizesAndPreReserveOnce()
    {
        var source = BuildDirectStringDtoSource(1, 4, 16, 64);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));

        Ensure(CountOccurrences(generated, "internal static class __SharpLinkGeneratedUtf16") == 1,
            "one assembly-private UTF-16 helper must be shared by all eligible generated Codecs");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf16.GetByteCount(__string_") == 85,
            "each direct string must compute its exact UTF-16 byte count once in the direct reservation path");
        Ensure(CountOccurrences(generated, "checked(value.Length * sizeof(char))") == 1,
            "the known-size helper must compute UTF-16 bytes in O(1) without an encoding traversal");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf16.WriteStringKnownSize(writer, __string_") == 85,
            "each direct string must reuse its cached value and byte count in the direct write path");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf16.GetByteCount(__snapshot.__string_") == 85,
            "each direct string must be captured once for the snapshot sizing path");
        Ensure(CountOccurrences(generated, "__SharpLinkGeneratedUtf16.WriteStringKnownSize(buffer, __snapshot.__string_") == 85,
            "each direct string must reuse its snapshot value and byte count in the sized write path");
        Ensure(CountOccurrences(generated, "if (writer is IRpcByteBufferWriter __rpcWriter)") == 4,
            "each eligible DTO must gate whole-payload reservation on the SharpLink packet writer");
        Ensure(CountOccurrences(generated, "__rpcWriter.GetSpan(checked(__encodedSize + 4));") == 4,
            "each eligible DTO must make one capacity request including existing varuint request slack");
        Ensure(CountOccurrences(generated, "__rpcWriter.Advance(0);") == 4,
            "the discarded reservation must complete its buffer lease");
        Ensure(CountOccurrences(generated, "var __encodedSize =") == 4,
            "each eligible DTO must compute one checked encoded size");
        Ensure(!generated.Contains("RpcGeneratedCodecWire.WriteString(writer, value.Field", StringComparison.Ordinal),
            "eligible DTOs must not call the public string primitive after pre-sizing");
        Ensure(!generated.Contains("UTF8Encoding", StringComparison.Ordinal) &&
               !generated.Contains("StrictEncoding.GetByteCount", StringComparison.Ordinal),
            "generated DTO string sizing must not transcode or traverse UTF-8");
        Ensure(generated.Contains("global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian", StringComparison.Ordinal) &&
               generated.Contains("value.AsSpan().CopyTo(global::System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(payload));", StringComparison.Ordinal),
            "known-size writes must preserve the Int32 little-endian prefix and raw UTF-16 code-unit payload");
        return Task.CompletedTask;
    }

    [Test]
    public Task CyclicDtoGraphShouldReportSharplink010()
    {
        var source = BuildSource("""
public sealed class Node
{
    public Node? Next { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Node> Echo(Node value);
}
""");

        EnsureHasRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedDictionaryReaderShouldRejectNullKeysAsDataLoss()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IDictionaryContract : SharpLink.Sdk.IService
{
    ValueTask<Dictionary<string, string>> Echo(
        Dictionary<string, string> values,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("Generated dictionary contains a null key.", StringComparison.Ordinal),
            "generated dictionary readers must reject null keys before Dictionary.TryAdd");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsealedRecordDtoShouldBeRejectedBeforeDerivedStateCanBeSliced()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public record BasePayload(int Value);

public sealed record DerivedPayload(int Value, int Extra) : BasePayload(Value);
""");

        EnsureRuleCount(source, "SHARPLINK009", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task ByReferenceDtoConstructorsMustNotBeSelectedForGeneratedCalls()
    {
        var invalid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RefConstructorDto
{
    public int Value { get; }

    public RefConstructorDto(ref int value) => Value = value;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class RefReadonlyConstructorDto
{
    public int Value { get; }

    public RefReadonlyConstructorDto(ref readonly int value) => Value = value;
}
""");
        EnsureRuleCount(invalid, "SHARPLINK012", 2);

        var validFallback = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class FallbackConstructorDto
{
    public int Value { get; }

    public FallbackConstructorDto(ref int value) => Value = value;
    public FallbackConstructorDto(int value) => Value = value;
}
""");
        EnsureDoesNotHaveRule(validFallback, "SHARPLINK012");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedRequestSchemasShouldReportASpecificDiagnostic()
    {
        var nameAndTopLevelNullability = BuildSource("""
#nullable enable
public interface IRequiredNameBase
{
    ValueTask<int> Resolve(string requiredName, CancellationToken cancellationToken);
}

public interface IOptionalAliasBase
{
    ValueTask<int> Resolve(string? optionalAlias, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingRequestSchemaContract : SharpLink.Sdk.IService, IRequiredNameBase, IOptionalAliasBase
{
}
""");

        EnsureRuleCount(nameAndTopLevelNullability, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(nameAndTopLevelNullability)).Contains(
                "IConflictingRequestSchemaContractProxy",
                StringComparison.Ordinal),
            "conflicting inherited request schemas must not emit contract artifacts");

        var nestedNullability = BuildSource("""
#nullable enable
public interface IRequiredItemsBase
{
    ValueTask<int> Resolve(List<string> items, CancellationToken cancellationToken);
}

public interface IOptionalItemsBase
{
    ValueTask<int> Resolve(List<string?> items, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingNestedSchemaContract : SharpLink.Sdk.IService, IRequiredItemsBase, IOptionalItemsBase
{
}
""");
        EnsureRuleCount(nestedNullability, "SHARPLINK057", 1);

        var parameterNameOnly = BuildSource("""
public interface IPrimaryNameBase
{
    ValueTask<int> Resolve(string primaryName, CancellationToken cancellationToken);
}

public interface IAliasNameBase
{
    ValueTask<int> Resolve(string aliasName, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingNameSchemaContract : SharpLink.Sdk.IService, IPrimaryNameBase, IAliasNameBase
{
}
""");
        EnsureRuleCount(parameterNameOnly, "SHARPLINK057", 1);

        var controlParameterNames = BuildSource("""
public interface IFirstControlBase
{
    ValueTask<int> Resolve(string value, CancellationToken firstToken);
}

public interface ISecondControlBase
{
    ValueTask<int> Resolve(string value, CancellationToken secondToken);
}

[SharpLink.Sdk.RpcContract]
public interface ICompatibleControlNamesContract : SharpLink.Sdk.IService, IFirstControlBase, ISecondControlBase
{
}
""");
        EnsureDoesNotHaveRule(controlParameterNames, "SHARPLINK057");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManifestlessReferencedContractShouldNotCreateConsumerCodecManifest()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var contract = CreateMetadataReference(
            "ReferencedDtoContract",
            """
using System.Threading.Tasks;

namespace ReferencedDtoContract
{
    public sealed class Payload
    {
        public int Value { get; set; }
    }

    [SharpLink.Sdk.RpcContract]
    public interface ICodecContract : SharpLink.Sdk.IService
    {
        ValueTask<Payload> Echo(Payload value);
    }
}
""",
            sdk);

        var generated = RunGeneratorAndGetSources(
            "namespace CodecConsumer { public sealed class Marker; }",
            sdk,
            contract);
        Ensure(!generated.Any(static text =>
                text.Contains("__SharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal)),
            "a consumer with no owned generated artifacts must not publish a manifest for a referenced manifest-less Contract.");
        Ensure(!generated.Any(static text =>
                text.Contains("IRpcCodec<global::ReferencedDtoContract.Payload>", StringComparison.Ordinal)),
            "a referenced manifest-less Contract payload must not leak into the consumer Codec graph.");
        return Task.CompletedTask;
    }

    [Test]
    public Task InaccessibleGeneratedServiceAndDtoTypesShouldReportSharpLinkDiagnostics()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHiddenArtifactContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public static class HiddenArtifactContainer
{
    [SharpLink.Sdk.RpcService]
    private sealed class HiddenService : IHiddenArtifactContract
    {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
    }

    [SharpLink.Sdk.RpcSerializable]
    private sealed class HiddenDto
    {
        public int Value { get; set; }
    }
}
""");

        EnsureRuleCount(source, "SHARPLINK018", 1);
        EnsureRuleCount(source, "SHARPLINK009", 1);

        var allowed = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAllowedArtifactContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public class AllowedArtifactContainer
{
    [SharpLink.Sdk.RpcService]
    protected internal sealed class AllowedService : IAllowedArtifactContract
    {
        public AllowedService() { }
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
    }
}

[SharpLink.Sdk.RpcSerializable]
internal sealed class InternalDto
{
    public int Value { get; set; }
}
""");
        EnsureDoesNotHaveRule(allowed, "SHARPLINK018");
        EnsureDoesNotHaveRule(allowed, "SHARPLINK009");
        var generated = string.Join("\n", RunGeneratorAndGetSources(allowed));
        Ensure(generated.Contains("global::AllowedArtifactContainer.AllowedService", StringComparison.Ordinal),
            "protected-internal services must remain accessible to sibling generated code");
        Ensure(generated.Contains("global::InternalDto", StringComparison.Ordinal),
            "internal DTOs must remain accessible to sibling generated code");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedRequestWireFailuresMustUseStructuredDataLoss()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IRequestDataLossContract : SharpLink.Sdk.IService
{
    ValueTask<int> Validate(
        bool enabled,
        string name,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("throw new InvalidDataException", StringComparison.Ordinal),
            "peer-controlled generated request wire failures must not leak unstructured InvalidDataException");
        Ensure(CountOccurrences(generated, "throw RpcGeneratedCodecWire.DataLoss(") >= 8,
            "request Codec and Stub must classify marker, truncation, length, null, and trailing failures as DataLoss");
        return Task.CompletedTask;
    }

    [Test]
    public Task OptionalDtoMemberNullabilityAnnotationShouldNotPerturbRuntimeCodecHash()
    {
        var nonNullable = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IDtoSchemaContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Resolve(CancellationToken cancellationToken);
}
public sealed class Payload { public string Name { get; set; } = string.Empty; }
""");
        var nullable = BuildSource("""
#nullable enable
[SharpLink.Sdk.RpcContract]
public interface IDtoSchemaContract : SharpLink.Sdk.IService
{
    ValueTask<Payload> Resolve(CancellationToken cancellationToken);
}
public sealed class Payload { public string? Name { get; set; } }
""");

        var nonNullableHash = GetFirstGeneratedCodecHash(nonNullable);
        var nullableHash = GetFirstGeneratedCodecHash(nullable);
        Ensure(string.Equals(nonNullableHash, nullableHash, StringComparison.Ordinal),
            "optional nullable annotations must not change runtime CodecHash when generated null behavior is identical");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtosWithNestedMembersShouldComputeRecursiveExactSize()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class MixedPayload
{
    public string Name { get; set; } = string.Empty;
    public NestedPayload Nested { get; set; } = new();
}

[SharpLink.Sdk.RpcSerializable]
public sealed class NestedPayload
{
    public int Value { get; set; }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("internal static class __SharpLinkGeneratedUtf16", StringComparison.Ordinal) &&
               generated.Contains("out var __exactSize", StringComparison.Ordinal) &&
               generated.Contains("IRpcSizedCodec", StringComparison.Ordinal) &&
               generated.Contains("IRpcSizedCodecSnapshot", StringComparison.Ordinal) &&
               generated.Contains("TryGetEncodedSize", StringComparison.Ordinal) &&
               generated.Contains("SerializeSized", StringComparison.Ordinal),
            "a nested DTO with direct strings must compute a recursive exact size");
        Ensure(!generated.Contains("RpcGeneratedCodecWire.WriteString(writer, value.Name);", StringComparison.Ordinal),
            "direct strings in a partially pre-reserved DTO must use cached byte counts");
        Ensure(generated.Contains("RpcGeneratedCodecWire.BeginLength", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.EndLength", StringComparison.Ordinal),
            "nested members must still use length backfill instead of claiming an exact top-level size");
        return Task.CompletedTask;
    }

    [Test]
    public Task SemanticDtoMembersShouldUseValidatedCodecs()
    {
        var source = BuildSource("""
public sealed record SemanticPayload(
    [property: SharpLink.Sdk.RpcMember(1)] bool Boolean,
    [property: SharpLink.Sdk.RpcMember(2)] System.Text.Rune Rune,
    [property: SharpLink.Sdk.RpcMember(3)] decimal Decimal,
    [property: SharpLink.Sdk.RpcMember(4)] System.DateOnly DateOnly,
    [property: SharpLink.Sdk.RpcMember(5)] System.DateTime DateTime,
    [property: SharpLink.Sdk.RpcMember(6)] System.TimeOnly TimeOnly,
    [property: SharpLink.Sdk.RpcMember(7)] System.DateTimeOffset DateTimeOffset,
    [property: SharpLink.Sdk.RpcMember(8)] bool? NullableBoolean,
    [property: SharpLink.Sdk.RpcMember(9)] System.Text.Rune? NullableRune,
    [property: SharpLink.Sdk.RpcMember(10)] decimal? NullableDecimal,
    [property: SharpLink.Sdk.RpcMember(11)] System.DateOnly? NullableDateOnly,
    [property: SharpLink.Sdk.RpcMember(12)] System.DateTime? NullableDateTime,
    [property: SharpLink.Sdk.RpcMember(13)] System.TimeOnly? NullableTimeOnly,
    [property: SharpLink.Sdk.RpcMember(14)] System.DateTimeOffset? NullableDateTimeOffset);

[SharpLink.Sdk.RpcContract]
public interface ISemanticService : SharpLink.Sdk.IService
{
    ValueTask<SemanticPayload> Echo(SemanticPayload value);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("RpcGeneratedCodecWire.WriteBoolean(writer, value.Boolean)", StringComparison.Ordinal),
            "generated Boolean encoder must emit its canonical marker");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadBoolean(ref reader)", StringComparison.Ordinal),
            "generated Boolean decoder must validate its marker");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadRune(ref reader)", StringComparison.Ordinal),
            "Rune member must use its validated fixed reader");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadDecimal(ref reader)", StringComparison.Ordinal),
            "decimal member must use its validated fixed reader");
        Ensure(generated.Contains("RpcGeneratedCodecWire.ReadDateOnly(ref reader)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadDateTime(ref reader)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadTimeOnly(ref reader)", StringComparison.Ordinal),
            "temporal members must use their validated fixed readers");
        Ensure(generated.Contains("RpcGeneratedCodecWire.WriteDateTimeOffset(writer, value.DateTimeOffset)", StringComparison.Ordinal) &&
               generated.Contains("RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader)", StringComparison.Ordinal),
            "DateTimeOffset member must use its canonical fixed writer and validated reader");
        Ensure(CountOccurrences(generated, "RpcGeneratedCodecWire.ReadBoolean(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadRune(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDecimal(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateOnly(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateTime(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadTimeOnly(ref reader)") == 2 &&
               CountOccurrences(generated, "RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader)") == 2,
            "nullable semantic members must use the same validated readers");
        return Task.CompletedTask;
    }

    [Test]
    public Task DuplicateDtoMemberIdShouldReportSharplink011()
    {
        var source = BuildSource("""
public sealed class Collision
{
    [SharpLink.Sdk.RpcMember(1)] public int First { get; set; }
    [SharpLink.Sdk.RpcMember(1)] public int Second { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Collision> Echo(Collision value);
}
""");

        EnsureHasRule(source, "SHARPLINK011");
        return Task.CompletedTask;
    }

    [Test]
    public Task CaseInsensitiveDtoMemberAmbiguityShouldReportSharplink012()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class AmbiguousCaseDto
{
    public string Name { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"case-insensitive member names must not crash the Generator. Actual: {FormatDiagnostics(diagnostics)}");
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK012"),
            $"an assignable DTO with case-distinct members should remain supported. Actual: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("value.Name", StringComparison.Ordinal) &&
               generated.Contains("value.name", StringComparison.Ordinal),
            "both case-distinct members must remain in the generated Codec");

        var ambiguousConstructor = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class AmbiguousConstructorDto
{
    public string Name { get; }
    public string name { get; }

    public AmbiguousConstructorDto(string NAME)
    {
        Name = NAME;
        name = NAME;
    }
}
""");
        var constructorDiagnostics = RunGenerator(ambiguousConstructor);
        Ensure(!constructorDiagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"ambiguous constructor mapping must not crash the Generator. Actual: {FormatDiagnostics(constructorDiagnostics)}");
        EnsureRuleCount(ambiguousConstructor, "SHARPLINK012", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task KeywordDtoMembersShouldUseSafeGeneratedLocalNames()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class KeywordDto
{
    [SharpLink.Sdk.RpcRequired]
    public int @class { get; set; }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("local_@class", StringComparison.Ordinal),
            "escaped member syntax must not be embedded inside a generated local identifier");
        Ensure(!generated.Contains("seen_@class", StringComparison.Ordinal),
            "escaped member syntax must not be embedded inside a generated presence identifier");
        Ensure(generated.Contains("local_class", StringComparison.Ordinal) &&
               generated.Contains("seen_class", StringComparison.Ordinal) &&
               generated.Contains("value.@class", StringComparison.Ordinal),
            "generated locals and escaped member access must remain distinct");
        return Task.CompletedTask;
    }

    [Test]
    public Task IgnoredRequiredDtoMembersNeedACompilerValidConstructionPlan()
    {
        var invalid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class IgnoredRequiredDto
{
    public int Value { get; set; }

    [SharpLink.Sdk.RpcIgnore]
    public required string Secret { get; init; }
}
""");

        EnsureRuleCount(invalid, "SHARPLINK012", 1);

        var valid = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RequiredMembersSatisfiedDto
{
    public int Value { get; set; }

    [SharpLink.Sdk.RpcIgnore]
    public required string Secret { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RequiredMembersSatisfiedDto() => Secret = string.Empty;
}
""");
        EnsureDoesNotHaveRule(valid, "SHARPLINK012");

        var requiredField = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class RequiredFieldDto
{
    public required int Value;

    public RequiredFieldDto(int value) => Value = value;
}
""");
        EnsureDoesNotHaveRule(requiredField, "SHARPLINK012");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(requiredField)).Contains(
                "Value = local_Value",
                StringComparison.Ordinal),
            "a compiler-required field must remain in the generated object initializer even when constructor-bound");
        return Task.CompletedTask;
    }
}
