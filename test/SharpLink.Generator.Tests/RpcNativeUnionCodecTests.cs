using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NativeUnionShouldGenerateTypedCodecAndNestedDependencies()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(2, typeof(NumberCase))]
[SharpLink.Sdk.RpcUnionCase(1, typeof(TextCase))]
public interface IResultUnion
{
}

public sealed class TextCase : IResultUnion
{
    public string Value { get; set; } = string.Empty;
}

public sealed class NumberCase : IResultUnion
{
    public int Value { get; set; }
}

public sealed class Envelope
{
    public IResultUnion? Value { get; set; }
    public List<IResultUnion> Items { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IUnionContract : SharpLink.Sdk.IService
{
    ValueTask<Envelope> Echo(Envelope value);
}
""");

        var diagnostics = RunGenerator(source);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            $"native union generation reported errors: {FormatDiagnostics(diagnostics)}");
        Ensure(generated.Contains("IRpcCodec<global::IResultUnion>", StringComparison.Ordinal),
            "native union must emit a typed Codec");
        Ensure(generated.Contains("provider.GetCodec<global::TextCase>()", StringComparison.Ordinal),
            "union case Codec must come from the finalized typed provider");
        Ensure(generated.Contains("provider.GetCodec<global::NumberCase>()", StringComparison.Ordinal),
            "every union case must use the finalized typed provider");
        Ensure(generated.Contains("case 1U:", StringComparison.Ordinal) &&
               generated.Contains("case 2U:", StringComparison.Ordinal),
            "declared positive discriminators must be emitted deterministically");
        Ensure(generated.Contains("Native union discriminator is truncated.", StringComparison.Ordinal) &&
               generated.Contains("unknown discriminator", StringComparison.Ordinal),
            "malformed and unknown discriminators must become structured decode failures");
        Ensure(generated.Contains("IRpcCodec<global::System.Collections.Generic.List<global::IResultUnion>>", StringComparison.Ordinal),
            "collections nested over a union must resolve through the same final Codec graph");
        Ensure(GetGeneratedManifest(source).Contains(
                "SharpLinkGeneratedCodecIdentityAttribute(typeof(global::IResultUnion)",
                StringComparison.Ordinal),
            "the assembly manifest must publish the native union Codec identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task NativeUnionHashShouldBeOrderIndependentAndSemantic()
    {
        var ordered = NativeUnionContract(
            "[SharpLink.Sdk.RpcUnionCase(1, typeof(TextCase))]\n[SharpLink.Sdk.RpcUnionCase(2, typeof(NumberCase))]",
            "int");
        var reversed = NativeUnionContract(
            "[SharpLink.Sdk.RpcUnionCase(2, typeof(NumberCase))]\n[SharpLink.Sdk.RpcUnionCase(1, typeof(TextCase))]",
            "int");
        var retagged = NativeUnionContract(
            "[SharpLink.Sdk.RpcUnionCase(1, typeof(TextCase))]\n[SharpLink.Sdk.RpcUnionCase(3, typeof(NumberCase))]",
            "int");
        var childChanged = NativeUnionContract(
            "[SharpLink.Sdk.RpcUnionCase(1, typeof(TextCase))]\n[SharpLink.Sdk.RpcUnionCase(2, typeof(NumberCase))]",
            "long");

        var orderedHash = GetGeneratedCodecHashForType(ordered, "IResultUnion");
        Ensure(orderedHash == GetGeneratedCodecHashForType(reversed, "IResultUnion"),
            "attribute/source order must not change the native union CodecHash");
        Ensure(orderedHash != GetGeneratedCodecHashForType(retagged, "IResultUnion"),
            "changing a union discriminator must change the native union CodecHash");
        Ensure(orderedHash != GetGeneratedCodecHashForType(childChanged, "IResultUnion"),
            "changing a selected child CodecHash must change the native union CodecHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task NativeUnionHashShouldKeepCaseLogicalIdentitySeparateFromChildHash()
    {
        var caseA = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class CaseA
{
    public int Value { get; set; }
}
""");
        var caseB = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class CaseB
{
    public int Value { get; set; }
}
""");
        Ensure(GetGeneratedCodecHashForType(caseA, "CaseA") == GetGeneratedCodecHashForType(caseB, "CaseB"),
            "the control case types must intentionally share the same child wire CodecHash");

        var unionA = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(CaseA))]
public interface ILogicalUnion { }
public sealed class CaseA : ILogicalUnion { public int Value { get; set; } }
[SharpLink.Sdk.RpcContract]
public interface ILogicalContract : SharpLink.Sdk.IService
{
    ValueTask<ILogicalUnion> Echo(ILogicalUnion value);
}
""");
        var unionB = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(CaseB))]
public interface ILogicalUnion { }
public sealed class CaseB : ILogicalUnion { public int Value { get; set; } }
[SharpLink.Sdk.RpcContract]
public interface ILogicalContract : SharpLink.Sdk.IService
{
    ValueTask<ILogicalUnion> Echo(ILogicalUnion value);
}
""");

        Ensure(GetGeneratedCodecHashForType(unionA, "ILogicalUnion") !=
               GetGeneratedCodecHashForType(unionB, "ILogicalUnion"),
            "distinct CLR case identities must keep union hashes distinct even when child wire hashes are identical");
        return Task.CompletedTask;
    }

    [Test]
    public Task AmbiguousUnionInheritanceShouldFailEvenWhenUnionIsUnused()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(BaseCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(DerivedCase))]
public interface IAmbiguousUnion { }

public class BaseCase : IAmbiguousUnion { }
public sealed class DerivedCase : BaseCase { }
""");

        EnsureHasRuleContaining(source, "SHARPLINK009", "overlap by inheritance");
        return Task.CompletedTask;
    }

    private static string NativeUnionContract(string attributes, string numberType)
        => BuildSource($$"""
{{attributes}}
public interface IResultUnion { }
public sealed class TextCase : IResultUnion { public string Value { get; set; } = string.Empty; }
public sealed class NumberCase : IResultUnion { public {{numberType}} Value { get; set; } }
[SharpLink.Sdk.RpcContract]
public interface IUnionIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<IResultUnion> Echo(IResultUnion value);
}
""");

    private static string GetGeneratedCodecHashForType(string source, string typeName)
    {
        var manifest = GetGeneratedManifest(source);
        var marker = $"[assembly: SharpLinkGeneratedCodecIdentityAttribute(typeof(global::{typeName}), ";
        var start = manifest.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new Exception($"Expected generated Codec identity for '{typeName}'.");
        start += marker.Length;
        var end = manifest.IndexOf(")]", start, StringComparison.Ordinal);
        if (end < 0)
            throw new Exception($"Expected generated Codec identity terminator for '{typeName}'.");
        return manifest[start..end];
    }
}