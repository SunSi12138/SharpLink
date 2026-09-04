using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task DeterministicIdentityShouldBeStableAcrossRepeatedGeneration()
    {
        var first = GenerateDtoIdentityManifest(includeExtraMember: false, idempotent: false);
        var second = GenerateDtoIdentityManifest(includeExtraMember: false, idempotent: false);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "DeterministicPayload") ==
            ExtractGeneratedCodecIdentity(second, "DeterministicPayload"),
            "unchanged RPC semantics must produce the same CodecHash across repeated generation");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) == ExtractGeneratedRpcAssemblyHash(second),
            "unchanged RPC semantics must produce the same RpcAssemblyHash across repeated generation");
        return Task.CompletedTask;
    }

    [Test]
    public Task SameRpcSemanticsShouldProduceSameIdentityForX64AndX86()
    {
        var source = BuildDtoIdentitySource(includeExtraMember: false, idempotent: false);
        var x64 = GenerateIdentityManifest(
            "DeterministicIdentityPlatform",
            source,
            Platform.X64);
        var x86 = GenerateIdentityManifest(
            "DeterministicIdentityPlatform",
            source,
            Platform.X86);

        Ensure(
            ExtractGeneratedCodecIdentity(x64, "DeterministicPayload") ==
            ExtractGeneratedCodecIdentity(x86, "DeterministicPayload"),
            "CodecHash must not depend on x64 versus x86 compilation platform");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(x64) == ExtractGeneratedRpcAssemblyHash(x86),
            "RpcAssemblyHash must not depend on x64 versus x86 compilation platform");
        return Task.CompletedTask;
    }

    [Test]
    public Task SameApparentAbiInDifferentAssembliesShouldHaveDifferentAssemblyIdentity()
    {
        var source = BuildDtoIdentitySource(includeExtraMember: false, idempotent: false);
        var first = GenerateIdentityManifest(
            "DeterministicIdentityAssemblyA",
            source,
            Platform.AnyCpu);
        var second = GenerateIdentityManifest(
            "DeterministicIdentityAssemblyB",
            source,
            Platform.AnyCpu);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "DeterministicPayload") ==
            ExtractGeneratedCodecIdentity(second, "DeterministicPayload"),
            "the same payload definition must retain the same CodecHash across Contract assemblies");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "different Contract assembly logical identities must not collapse to one RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtoWireShapeChangeShouldChangeFinalRpcIdentity()
    {
        var first = GenerateDtoIdentityManifest(includeExtraMember: false, idempotent: false);
        var second = GenerateDtoIdentityManifest(includeExtraMember: true, idempotent: false);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "DeterministicPayload") !=
            ExtractGeneratedCodecIdentity(second, "DeterministicPayload"),
            "changing generated DTO wire shape must change CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "changing a reachable DTO CodecHash must change RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task MethodSemanticChangeShouldNotReuseRouteIdentityAsCompatibilityIdentity()
    {
        var first = GenerateDtoIdentityManifest(includeExtraMember: false, idempotent: false);
        var second = GenerateDtoIdentityManifest(includeExtraMember: false, idempotent: true);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "DeterministicPayload") ==
            ExtractGeneratedCodecIdentity(second, "DeterministicPayload"),
            "method-only semantics must not perturb payload CodecHash");
        Ensure(
            ExtractGeneratedMethodId(first, "Echo") == ExtractGeneratedMethodId(second, "Echo"),
            "a method semantic flag must not be encoded by changing the dispatch MethodId");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "method semantic changes must flow through MethodHash/ContractHash into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task OpaqueSemanticIdentityShouldIgnoreUnrelatedImplementationChanges()
    {
        var first = GenerateOpaqueIdentityManifest(
            implementationMarker: "first-build",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);
        var second = GenerateOpaqueIdentityManifest(
            implementationMarker: "second-build",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "OpaquePayload") ==
            ExtractGeneratedCodecIdentity(second, "OpaquePayload"),
            "opaque CodecHash must be controlled by its fixed semantic identity rather than unrelated implementation details");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) == ExtractGeneratedRpcAssemblyHash(second),
            "unrelated implementation changes must not perturb RpcAssemblyHash when RPC semantics are unchanged");
        return Task.CompletedTask;
    }

    [Test]
    public Task OpaqueSemanticIdentityChangeShouldChangeFinalRpcIdentity()
    {
        var first = GenerateOpaqueIdentityManifest(
            implementationMarker: "same-implementation",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);
        var second = GenerateOpaqueIdentityManifest(
            implementationMarker: "same-implementation",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x2112131415161718UL);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "OpaquePayload") !=
            ExtractGeneratedCodecIdentity(second, "OpaquePayload"),
            "changing opaque serializer semantics must change CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "changing a payload CodecHash must flow through MethodHash/ContractHash into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsafeBlitFieldRenameShouldPreserveIdentity()
    {
        var first = GenerateUnsafeBlitIdentityManifest("First", "Second", "long");
        var renamed = GenerateUnsafeBlitIdentityManifest("RenamedFirst", "RenamedSecond", "long");

        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) == ExtractGeneratedRpcAssemblyHash(renamed),
            "field renames that preserve UnsafeBlit bytes must preserve the CodecHash-derived RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnsafeBlitPhysicalLayoutChangeShouldChangeIdentity()
    {
        var first = GenerateUnsafeBlitIdentityManifest("First", "Second", "long");
        var changed = GenerateUnsafeBlitIdentityManifest("First", "Second", "int");

        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(changed),
            "changing UnsafeBlit physical layout must change the CodecHash-derived RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task NativeSizedUnsafeBlitShouldUseStable64BitOnlyIdentity()
    {
        var nativeSource = BuildSource("""
public struct NativeSizedUnsafeLayoutPayload
{
    public int Prefix;
    public nint Handle;
}

[SharpLink.Sdk.RpcContract]
public interface INativeSizedUnsafeLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<NativeSizedUnsafeLayoutPayload> Echo(
        NativeSizedUnsafeLayoutPayload value,
        CancellationToken cancellationToken);
}
""");
        var fixed64Source = nativeSource.Replace("public nint Handle;", "public long Handle;", StringComparison.Ordinal);

        var x64 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            nativeSource,
            Platform.X64);
        var x86 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            nativeSource,
            Platform.X86);
        var fixed64 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            fixed64Source,
            Platform.X64);

        Ensure(
            ExtractGeneratedRpcAssemblyHash(x64) == ExtractGeneratedRpcAssemblyHash(x86),
            "native-sized UnsafeBlit identity must describe the supported 64-bit wire layout independently of compiler platform");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(x64) != ExtractGeneratedRpcAssemblyHash(fixed64),
            "native-sized UnsafeBlit identity must remain distinct from a fixed-width Int64 field");
        return Task.CompletedTask;
    }

    [Test]
    public Task SharedPayloadShouldHaveSameCodecHashAcrossContractAssemblies()
    {
        const string sharedPayload = """
namespace SharedPayloadModels
{
    [SharpLink.Sdk.RpcSerializable]
    public sealed class SharedPayload
    {
        public int Value { get; set; }
    }
}
""";
        var firstSource = BuildSource(sharedPayload + """

[SharpLink.Sdk.RpcContract]
public interface IFirstSharedPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<SharedPayloadModels.SharedPayload> Echo(
        SharedPayloadModels.SharedPayload value,
        CancellationToken cancellationToken);
}
""");
        var secondSource = BuildSource(sharedPayload + """

[SharpLink.Sdk.RpcContract]
public interface ISecondSharedPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<SharedPayloadModels.SharedPayload> Echo(
        SharedPayloadModels.SharedPayload value,
        CancellationToken cancellationToken);
}
""");

        var first = GenerateIdentityManifest(
            "FirstSharedPayloadContracts",
            firstSource,
            Platform.AnyCpu);
        var second = GenerateIdentityManifest(
            "SecondSharedPayloadContracts",
            secondSource,
            Platform.AnyCpu);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "SharedPayloadModels.SharedPayload") ==
            ExtractGeneratedCodecIdentity(second, "SharedPayloadModels.SharedPayload"),
            "the same payload definition must publish one CodecHash across different Contract assemblies");
        return Task.CompletedTask;
    }

    private static string GenerateDtoIdentityManifest(bool includeExtraMember, bool idempotent)
    {
        var source = BuildDtoIdentitySource(includeExtraMember, idempotent);
        return RunGeneratorAndGetSources(source)
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }

    private static string BuildDtoIdentitySource(bool includeExtraMember, bool idempotent)
    {
        var extraMember = includeExtraMember
            ? "public long Extra { get; set; }"
            : string.Empty;
        var methodAttribute = idempotent ? "[SharpLink.Sdk.Idempotent]" : string.Empty;
        return BuildSource($$"""
namespace SharpLink.Sdk
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class IdempotentAttribute : Attribute { }
}

[SharpLink.Sdk.RpcSerializable]
public sealed class DeterministicPayload
{
    public int Value { get; set; }
    {{extraMember}}
}

[SharpLink.Sdk.RpcContract]
public interface IDeterministicIdentityContract : SharpLink.Sdk.IService
{
    {{methodAttribute}}
    ValueTask<DeterministicPayload> Echo(DeterministicPayload value, CancellationToken cancellationToken);
}
""");
    }

    private static string GenerateOpaqueIdentityManifest(
        string implementationMarker,
        ulong semanticHigh,
        ulong semanticLow)
    {
        var source = BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
[SharpLink.Sdk.RpcCodec(typeof(OpaquePayloadCodec))]
public sealed class OpaquePayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity({{semanticHigh}}UL, {{semanticLow}}UL)]
public sealed class OpaquePayloadCodec : SharpLink.Abstractions.IRpcCodec<OpaquePayload>
{
    private const string ImplementationMarker = "{{implementationMarker}}";

    public void Serialize(in OpaquePayload value, System.Buffers.IBufferWriter<byte> buffer) { _ = ImplementationMarker; }
    public OpaquePayload Deserialize(in System.Buffers.ReadOnlySequence<byte> buffer) => new();
}

[SharpLink.Sdk.RpcContract]
public interface IOpaqueIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<OpaquePayload> Echo(OpaquePayload value, CancellationToken cancellationToken);
}
""");

        return RunGeneratorAndGetSources(source)
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }

    private static string GenerateUnsafeBlitIdentityManifest(
        string firstFieldName,
        string secondFieldName,
        string secondFieldType)
    {
        var source = BuildSource($$"""
public struct UnsafeLayoutPayload
{
    public int {{firstFieldName}};
    public {{secondFieldType}} {{secondFieldName}};
}

[SharpLink.Sdk.RpcContract]
public interface IUnsafeLayoutIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<UnsafeLayoutPayload> Echo(UnsafeLayoutPayload value, CancellationToken cancellationToken);
}
""");

        return RunGeneratorAndGetSources(source)
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }

    private static string GenerateIdentityManifest(
        string assemblyName,
        string source,
        Platform platform,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            GetPlatformReferences().Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithPlatform(platform));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }

    private static string ExtractGeneratedCodecIdentity(string manifest, string typeName)
        => manifest.Split('\n')
            .Single(line =>
                line.Contains(
                    $"SharpLinkGeneratedCodecIdentityAttribute(typeof(global::{typeName})",
                    StringComparison.Ordinal))
            .Trim();

    private static string ExtractGeneratedMethodId(string manifest, string methodName)
    {
        var lines = manifest.Split('\n');
        for (var index = 0; index + 2 < lines.Length; index++)
        {
            if (lines[index].Contains("new SharpLinkGeneratedMethodDescriptor(", StringComparison.Ordinal) &&
                lines[index + 1].Contains($"\"{methodName}\"", StringComparison.Ordinal))
            {
                return lines[index + 2].Trim();
            }
        }

        throw new InvalidOperationException($"Generated method descriptor '{methodName}' was not found.");
    }

    private static string ExtractGeneratedRpcAssemblyHash(string manifest)
        => manifest.Split('\n')
            .Single(static line => line.Contains("public RpcHash128 RpcAssemblyHash =>", StringComparison.Ordinal))
            .Trim();
}
