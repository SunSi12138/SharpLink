using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NestedEnumDeclarationShouldParticipateInUnsafeBlitPhysicalIdentity()
    {
        static string Manifest(string members)
        {
            var source = BuildSource($$"""
public enum NestedPhysicalStatus : int
{
    {{members}}
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NestedEnumPhysicalPayload
{
    public int Prefix;
    public NestedPhysicalStatus Status;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class NestedEnumPhysicalEnvelope
{
    public NestedEnumPhysicalPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface INestedEnumPhysicalContract : SharpLink.Sdk.IService
{
    ValueTask<NestedEnumPhysicalEnvelope> Echo(
        NestedEnumPhysicalEnvelope value,
        CancellationToken cancellationToken);
}
""");

            return RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));
        }

        var before = Manifest("Ready = 0, Failed = 1");
        var after = Manifest("Ready = 1, Failed = 0");

        Ensure(
            ExtractGeneratedCodecIdentity(before, "NestedEnumPhysicalEnvelope") !=
            ExtractGeneratedCodecIdentity(after, "NestedEnumPhysicalEnvelope"),
            "a nested enum declaration mapping change must change an enclosing generated CodecHash even when width and struct layout are unchanged");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(before) != ExtractGeneratedRpcAssemblyHash(after),
            "nested enum declaration semantics must flow through the enclosing UnsafeBlit CodecHash into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsafeBlitIdentityShouldCanonicalizeEffectiveLayout()
    {
        static string Manifest(string source)
            => RunGeneratorAndGetSources(BuildSource(source))
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));

        static string SequentialSource(string charSet, int pack) => $$"""
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential,
    CharSet = System.Runtime.InteropServices.CharSet.{{charSet}},
    Pack = {{pack}})]
public struct EffectiveLayoutPayload
{
    public byte Head;
    public long Tail;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class EffectiveLayoutEnvelope
{
    public EffectiveLayoutPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IEffectiveLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<EffectiveLayoutEnvelope> Echo(
        EffectiveLayoutEnvelope value,
        CancellationToken cancellationToken);
}
""";

        static string ExplicitSource(bool reverseDeclarations, int tailOffset, int size)
        {
            var fields = reverseDeclarations
                ? $$"""
    [System.Runtime.InteropServices.FieldOffset({{tailOffset}})] public long Tail;
    [System.Runtime.InteropServices.FieldOffset(0)] public byte Head;
"""
                : $$"""
    [System.Runtime.InteropServices.FieldOffset(0)] public byte Head;
    [System.Runtime.InteropServices.FieldOffset({{tailOffset}})] public long Tail;
""";
            return $$"""
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Explicit,
    Size = {{size}})]
public struct EffectiveLayoutPayload
{
{{fields}}
}

[SharpLink.Sdk.RpcSerializable]
public sealed class EffectiveLayoutEnvelope
{
    public EffectiveLayoutPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IEffectiveLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<EffectiveLayoutEnvelope> Echo(
        EffectiveLayoutEnvelope value,
        CancellationToken cancellationToken);
}
""";
        }

        var sequentialAnsi = Manifest(SequentialSource("Ansi", 8));
        var sequentialUnicode = Manifest(SequentialSource("Unicode", 8));
        Ensure(
            ExtractGeneratedCodecIdentity(sequentialAnsi, "EffectiveLayoutEnvelope") ==
            ExtractGeneratedCodecIdentity(sequentialUnicode, "EffectiveLayoutEnvelope"),
            "StructLayout CharSet is source metadata but does not change raw unmanaged field layout and must not perturb an enclosing generated CodecHash");

        var explicitDeclaredForward = Manifest(ExplicitSource(reverseDeclarations: false, tailOffset: 8, size: 16));
        var explicitDeclaredReverse = Manifest(ExplicitSource(reverseDeclarations: true, tailOffset: 8, size: 16));
        Ensure(
            ExtractGeneratedCodecIdentity(explicitDeclaredForward, "EffectiveLayoutEnvelope") ==
            ExtractGeneratedCodecIdentity(explicitDeclaredReverse, "EffectiveLayoutEnvelope"),
            "Explicit-layout field declaration order must canonicalize by effective offset and physical semantics");

        var sequentialPack1 = Manifest(SequentialSource("Ansi", 1));
        Ensure(
            ExtractGeneratedCodecIdentity(sequentialAnsi, "EffectiveLayoutEnvelope") !=
            ExtractGeneratedCodecIdentity(sequentialPack1, "EffectiveLayoutEnvelope"),
            "an effective Sequential Pack change must change the propagated UnsafeBlit identity");

        var explicitOffsetChanged = Manifest(ExplicitSource(reverseDeclarations: false, tailOffset: 4, size: 16));
        Ensure(
            ExtractGeneratedCodecIdentity(explicitDeclaredForward, "EffectiveLayoutEnvelope") !=
            ExtractGeneratedCodecIdentity(explicitOffsetChanged, "EffectiveLayoutEnvelope"),
            "an effective Explicit field offset change must change the propagated UnsafeBlit identity");

        var explicitSizeChanged = Manifest(ExplicitSource(reverseDeclarations: false, tailOffset: 8, size: 24));
        Ensure(
            ExtractGeneratedCodecIdentity(explicitDeclaredForward, "EffectiveLayoutEnvelope") !=
            ExtractGeneratedCodecIdentity(explicitSizeChanged, "EffectiveLayoutEnvelope"),
            "an effective Explicit Size change must change the propagated UnsafeBlit identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullableEnumDtoMemberShouldRetainEnumDeclarationIdentity()
    {
        static string Manifest(string members)
        {
            var source = BuildSource($$"""
public enum NullableMemberStatus : int
{
    {{members}}
}

[SharpLink.Sdk.RpcSerializable]
public sealed class NullableEnumEnvelope
{
    public NullableMemberStatus? Status { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface INullableEnumMemberContract : SharpLink.Sdk.IService
{
    ValueTask<NullableEnumEnvelope> Echo(
        NullableEnumEnvelope value,
        CancellationToken cancellationToken);
}
""");

            return RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));
        }

        var before = Manifest("Ready = 0, Failed = 1");
        var after = Manifest("Ready = 1, Failed = 0");

        Ensure(
            ExtractGeneratedCodecIdentity(before, "NullableEnumEnvelope") !=
            ExtractGeneratedCodecIdentity(after, "NullableEnumEnvelope"),
            "Nullable<enum> used as a generated DTO member must retain enum declaration semantics in the parent CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(before) != ExtractGeneratedRpcAssemblyHash(after),
            "Nullable<enum> DTO member declaration semantics must flow into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task AutoLayoutDiagnosticShouldTraverseFinalCollectionAndGeneratedDtoPlans()
    {
        var collectionSource = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct CollectionAutoPayload
{
    public byte Head;
    public long Tail;
}

[SharpLink.Sdk.RpcContract]
public interface ICollectionAutoLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<List<CollectionAutoPayload>> Echo(
        List<CollectionAutoPayload> value,
        CancellationToken cancellationToken);
}
""");
        var collectionDiagnostic = RunUnsafeBlitCompatibilityGenerator(collectionSource)
            .Single(static diagnostic => diagnostic.Id == "SHARPLINK064");
        var collectionMessage = collectionDiagnostic.GetMessage();
        Ensure(
            collectionMessage.Contains("List", StringComparison.Ordinal) &&
            collectionMessage.Contains("CollectionAutoPayload", StringComparison.Ordinal),
            $"SHARPLINK064 must traverse a finalized collection Codec to its UnsafeBlit element plan. Actual: {collectionMessage}");

        var generatedDtoSource = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct DtoAutoPayload
{
    public short Code;
    public long Value;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class AutoLayoutEnvelope
{
    public DtoAutoPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGeneratedDtoAutoLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<AutoLayoutEnvelope> Echo(
        AutoLayoutEnvelope value,
        CancellationToken cancellationToken);
}
""");
        var generatedDtoDiagnostic = RunUnsafeBlitCompatibilityGenerator(generatedDtoSource)
            .Single(static diagnostic => diagnostic.Id == "SHARPLINK064");
        var generatedDtoMessage = generatedDtoDiagnostic.GetMessage();
        Ensure(
            generatedDtoMessage.Contains("AutoLayoutEnvelope", StringComparison.Ordinal) &&
            generatedDtoMessage.Contains("DtoAutoPayload", StringComparison.Ordinal),
            $"SHARPLINK064 must traverse a finalized generated DTO Codec to its UnsafeBlit member plan. Actual: {generatedDtoMessage}");
        return Task.CompletedTask;
    }

    [Test]
    public Task FunctionPointerSignatureShouldParticipateInUnsafeBlitIdentity()
    {
        static string Manifest(string signature)
            => GenerateUnsafeFinalPlanManifest(BuildSource($$"""
public unsafe struct FunctionPointerPayload
{
    public {{signature}} Callback;
}

[SharpLink.Sdk.RpcSerializable]
public sealed class FunctionPointerEnvelope
{
    public FunctionPointerPayload Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IFunctionPointerIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<FunctionPointerEnvelope> Echo(
        FunctionPointerEnvelope value,
        CancellationToken cancellationToken);
}
"""));

        var baseline = Manifest("delegate*<int, int>");
        var baselineCodec = ExtractGeneratedCodecIdentity(baseline, "FunctionPointerEnvelope");
        var baselineAssembly = ExtractGeneratedRpcAssemblyHash(baseline);
        foreach (var changedSignature in new[]
                 {
                     "delegate*<long, int>",
                     "delegate*<int, long>",
                     "delegate*<ref int, int>",
                     "delegate*<int, ref int>",
                     "delegate* unmanaged<int, int>"
                 })
        {
            var changed = Manifest(changedSignature);
            Ensure(
                baselineCodec != ExtractGeneratedCodecIdentity(changed, "FunctionPointerEnvelope"),
                $"function-pointer signature semantic '{changedSignature}' must change an enclosing generated CodecHash");
            Ensure(
                baselineAssembly != ExtractGeneratedRpcAssemblyHash(changed),
                $"function-pointer signature semantic '{changedSignature}' must flow into RpcAssemblyHash");
        }
        return Task.CompletedTask;
    }

    private static string GenerateUnsafeFinalPlanManifest(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            "FinalCodecPlanUnsafeAcceptance",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true));
        var sourceErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(
            sourceErrors.Length == 0,
            "unsafe acceptance source must compile: " +
            string.Join(Environment.NewLine, sourceErrors.Select(static diagnostic => diagnostic.ToString())));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        var runResult = driver.GetRunResult();
        var generatorErrors = runResult.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(
            generatorErrors.Length == 0,
            "unsafe acceptance generator run must succeed: " +
            string.Join(Environment.NewLine, generatorErrors.Select(static diagnostic => diagnostic.ToString())));

        return runResult.GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));
    }
}
