using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NullableReferencedUnionShouldPreserveNullableCodecType()
    {
        var support = CreateMetadataReference(
            "ReferencedUnionSupport",
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
        var owner = CreateMetadataReference(
            "ReferencedUnionOwner",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: SharpLinkGeneratedCodecIdentityAttribute(typeof(ReferencedUnion.IValue), 11UL, 12UL)]
[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(ReferencedUnion.Manifest),
    4,
    2,
    "2.0.0",
    "sharplink-2.0-api4-rpcchannel-codec-provider-v4")]

namespace ReferencedUnion
{
    [RpcUnionCase(1, typeof(ValueCase))]
    public interface IValue { }

    public sealed class ValueCase : IValue { }
    public sealed class Manifest { }
}
""",
            support);
        var source = BuildSource("""
#nullable enable
public sealed class ReferencedUnionEnvelope
{
    public ReferencedUnion.IValue? Current { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IReferencedUnionContract : SharpLink.Sdk.IService
{
    ValueTask<ReferencedUnionEnvelope> Echo(
        ReferencedUnionEnvelope value,
        CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source, support, owner);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            $"referenced union fixture should be analyzable: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source, support, owner));
        Ensure(generated.Contains(
                "IRpcCodec<global::ReferencedUnion.IValue?> __codec_0;",
                StringComparison.Ordinal),
            "nullable referenced union members must retain nullable child Codec typing without reconstructing the referenced union Codec locally");
    }
}
