using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ReferencedUnsafeBlitMetadataChangesShouldBreakCompatibility()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));

        MetadataReference PayloadReference(string privateFieldType, int pack) => CreateMetadataReference(
            "ExternalRawPayloads",
            $$"""
using SharpLink.Sdk;
using System.Runtime.InteropServices;

namespace ExternalRawPayloads
{
    [StructLayout(LayoutKind.Sequential, Pack = {{pack}})]
    public struct RawPayload
    {
        public int Value;
        private {{privateFieldType}} _state;
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
public interface IExternalRawContract : IService
{
    ValueTask<ExternalRawPayloads.RawPayload> Echo(
        ExternalRawPayloads.RawPayload value,
        CancellationToken cancellationToken);
}
""";

        var v1 = PayloadReference("int", 1);
        var v2 = PayloadReference("long", 1);
        var v3 = PayloadReference("int", 8);
        var baseline = RunContractGeneratorWithReferences(source, null, sdk, v1);
        var privateFieldChanged = RunContractGeneratorWithReferences(source, baseline.Json, sdk, v2);
        var packChanged = RunContractGeneratorWithReferences(source, baseline.Json, sdk, v3);

        Ensure(privateFieldChanged.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "referenced UnsafeBlit identity must fail closed when metadata-private layout changes");
        Ensure(packChanged.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "referenced UnsafeBlit identity must fail closed when PE layout metadata changes");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullableUnsafeBlitShouldIncludeUnderlyingLayout()
    {
        static string Source(string fieldType) => BuildSource($$"""
public struct ReviewPoint
{
    public {{fieldType}} Value;
}

[SharpLink.Sdk.RpcContract]
public interface INullableRawContract : SharpLink.Sdk.IService
{
    ValueTask<ReviewPoint?> Echo(ReviewPoint? value, CancellationToken cancellationToken);
}
""");

        var baseline = RunContractGenerator(Source("int")).Json;
        var changed = RunContractGenerator(Source("long"), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "Nullable<T> UnsafeBlit identity must include the closed underlying T layout");
        return Task.CompletedTask;
    }

    [Test]
    public Task DeepUnsafeBlitLayoutShouldNotTruncateCompatibilityIdentity()
    {
        static string Source(string leafType)
        {
            var source = new StringBuilder();
            for (var index = 0; index < 65; index++)
                source.Append("public struct S").Append(index).Append(" { public S").Append(index + 1).AppendLine(" V; }");
            source.Append("public struct S65 { public ").Append(leafType).AppendLine(" Value; }");
            source.AppendLine("[SharpLink.Sdk.RpcContract]");
            source.AppendLine("public interface IDeepRawContract : SharpLink.Sdk.IService");
            source.AppendLine("{");
            source.AppendLine("    ValueTask<S0> Echo(S0 value, CancellationToken cancellationToken);");
            source.AppendLine("}");
            return BuildSource(source.ToString());
        }

        var baseline = RunContractGenerator(Source("int")).Json;
        var changed = RunContractGenerator(Source("long"), baseline);
        Ensure(changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "UnsafeBlit compatibility identity must include wire-relevant fields beyond the DTO depth limit");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasBindingsShouldDiagnoseConflictingAdapters()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAliasConflictContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}

public sealed class AliasAdapterA : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "alias-a/v1";
    public string WireFormatId => "alias-a-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class AliasAdapterB : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "alias-b/v1";
    public string WireFormatId => "alias-b-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AliasAdapterA), \"alias-a/v1\", \"alias-a-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AliasAdapterB), \"alias-b/v1\", \"alias-b-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<(int X, int Y)>), typeof(AliasAdapterA))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<ValueTuple<int, int>>), typeof(AliasAdapterB))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK045"),
            "different explicit Adapter bindings for one canonical CLR target must report a selection conflict");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasCustomCodecShouldValidateAgainstClrIdentity()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcCodecImplementation("alias-custom-wire/v1", "alias-custom-schema/v1")]
public sealed class AliasCustomCodec : SharpLink.Abstractions.IRpcCodec<List<(int X, int Y)>>
{
}

[SharpLink.Sdk.RpcContract]
public interface IAliasCustomContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<ValueTuple<int, int>>), typeof(AliasCustomCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK060"),
            "custom IRpcCodec<T> validation must use canonical CLR identity for nested tuple aliases");
        var root = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(source).Json)!.AsObject();
        Ensure(root["codecs"]!.AsArray().Any(static item =>
                item!["kind"]!.GetValue<string>() == "Custom"),
            "canonical custom binding must be selected for the tuple-alias payload");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasDirectCodecShouldValidateAgainstClrIdentity()
    {
        var source = AddAssemblyAttributes(BuildSource("""
public sealed class AliasDirectCodec : SharpLink.Abstractions.IRpcCodec<List<(int X, int Y)>>
{
}

[SharpLink.Sdk.RpcContract]
public interface IAliasDirectContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<ValueTuple<int, int>>), typeof(AliasDirectCodec), WireFormatId = \"alias-direct-wire/v1\")]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id is "SHARPLINK043" or "SHARPLINK046"),
            "direct IRpcCodec<T> validation must use canonical CLR identity for nested tuple aliases");
        var root = System.Text.Json.Nodes.JsonNode.Parse(RunContractGenerator(source).Json)!.AsObject();
        Ensure(root["codecs"]!.AsArray().Any(static item =>
                item!["kind"]!.GetValue<string>() == "Direct"),
            "canonical direct binding must be selected for the tuple-alias payload");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasBindingsShouldDiagnoseConflictingCustomCodecs()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcCodecImplementation("alias-custom-a/v1", "alias-custom-a-schema/v1")]
public sealed class AliasCustomCodecA : SharpLink.Abstractions.IRpcCodec<List<(int X, int Y)>> { }

[SharpLink.Sdk.RpcCodecImplementation("alias-custom-b/v1", "alias-custom-b-schema/v1")]
public sealed class AliasCustomCodecB : SharpLink.Abstractions.IRpcCodec<List<ValueTuple<int, int>>> { }

[SharpLink.Sdk.RpcContract]
public interface IAliasCustomConflictContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<(int X, int Y)>), typeof(AliasCustomCodecA))]",
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<ValueTuple<int, int>>), typeof(AliasCustomCodecB))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK062"),
            "different custom Codec bindings for one canonical CLR target must report a selection conflict");
        return Task.CompletedTask;
    }
}
