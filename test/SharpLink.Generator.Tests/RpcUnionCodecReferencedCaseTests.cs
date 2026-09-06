using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NativeUnionCodecShouldHonorReferencedCaseCodecIdentityAndAbi()
    {
        var support = CreateMetadataReference(
            "ReferencedCaseSupport",
            """
using System;

namespace SharpLink.Sdk
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class RpcUnionCaseAttribute : Attribute
    {
        public RpcUnionCaseAttribute(int tag, Type caseType) { }
    }
}

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SharpLinkGeneratedCodecIdentityAttribute : Attribute
    {
        public SharpLinkGeneratedCodecIdentityAttribute(Type targetType, ulong high, ulong low) { }
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion,
            string abiIdentity) { }
    }
}
""");
        var firstOwner = CreateReferencedUnionCaseOwner(
            support,
            101UL,
            202UL,
            "sharplink-2.0-api4-rpcchannel-codec-provider-v4");
        var secondOwner = CreateReferencedUnionCaseOwner(
            support,
            303UL,
            404UL,
            "sharplink-2.0-api4-rpcchannel-codec-provider-v4");
        var incompatibleOwner = CreateReferencedUnionCaseOwner(
            support,
            101UL,
            202UL,
            "legacy-generated-abi");
        var consumer = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IReferencedCaseContract : SharpLink.Sdk.IService
{
    ValueTask<ReferencedCaseUnion.IValue> Echo(
        ReferencedCaseUnion.IValue value,
        CancellationToken cancellationToken);
}
""");

        var firstDiagnostics = RunGenerator(consumer, support, firstOwner);
        Ensure(!firstDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            $"referenced case CodecHash should resolve through the native union: {FormatDiagnostics(firstDiagnostics)}");
        var firstGenerated = string.Join("\n", RunGeneratorAndGetSources(consumer, support, firstOwner));
        Ensure(firstGenerated.Contains("typeof(global::ReferencedCaseUnion.ValueCase)", StringComparison.Ordinal) &&
               firstGenerated.Contains("new RpcHash128(101UL, 202UL)", StringComparison.Ordinal),
            "generated manifests must retain the referenced case's exact typed CodecHash dependency");
        Ensure(GetUnionCodecHashWithReferences(consumer, support, firstOwner) !=
               GetUnionCodecHashWithReferences(consumer, support, secondOwner),
            "changing only a referenced case CodecHash must change the containing union CodecHash");

        var incompatibleDiagnostics = RunGenerator(consumer, support, incompatibleOwner);
        Ensure(incompatibleDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("incompatible SharpLink generated ABI", StringComparison.Ordinal)),
            $"referenced case Codec metadata with the wrong ABI must fail closed: {FormatDiagnostics(incompatibleDiagnostics)}");
    }

    private static MetadataReference CreateReferencedUnionCaseOwner(
        MetadataReference support,
        ulong high,
        ulong low,
        string abiIdentity)
        => CreateMetadataReference(
            "ReferencedCaseOwner",
            $$"""
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: SharpLinkGeneratedCodecIdentityAttribute(typeof(ReferencedCaseUnion.ValueCase), {{high}}UL, {{low}}UL)]
[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(ReferencedCaseUnion.Manifest), 4, 2, "2.0.0", "{{abiIdentity}}")] // fixture ABI

namespace ReferencedCaseUnion
{
    [RpcUnionCase(1, typeof(ValueCase))]
    public interface IValue { }

    public sealed class ValueCase : IValue
    {
        public int Value { get; set; }
    }

    public sealed class Manifest { }
}
""",
            support);

    private static string GetUnionCodecHashWithReferences(
        string source,
        params MetadataReference[] references)
    {
        const string unionType = "global::ReferencedCaseUnion.IValue";
        var generated = RunGeneratorAndGetSources(source, references)
            .Single(text => text.Contains(
                "public Type TargetType => typeof(" + unionType + ");",
                StringComparison.Ordinal));
        var target = "public Type TargetType => typeof(" + unionType + ");";
        var targetIndex = generated.IndexOf(target, StringComparison.Ordinal);
        var hashStart = generated.IndexOf("public RpcHash128 CodecHash =>", targetIndex, StringComparison.Ordinal);
        Ensure(hashStart >= 0, "missing generated referenced-case union CodecHash");
        var hashEnd = generated.IndexOf('\n', hashStart);
        return generated[hashStart..(hashEnd < 0 ? generated.Length : hashEnd)].Trim();
    }
}
