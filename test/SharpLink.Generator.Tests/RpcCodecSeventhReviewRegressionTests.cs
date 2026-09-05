using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task NestedRuntimeSizedVectorShouldRequireExplicitCodec()
    {
        var source = BuildSource("""
public struct VectorWrapper
{
    private System.Numerics.Vector<int> _value;
}

[SharpLink.Sdk.RpcContract]
public interface IVectorWrapperContract : SharpLink.Sdk.IService
{
    ValueTask<VectorWrapper> Echo(VectorWrapper value, CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(
            diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("runtime-sized intrinsic unmanaged types", StringComparison.Ordinal)),
            $"an UnsafeBlit wrapper containing a private Vector<T> field must be rejected. Diagnostics: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterOwnedSchemaChangeShouldRequireSemanticIdentityBump()
    {
        static string Source(bool includeExtraMember)
        {
            var extraMember = includeExtraMember ? "public long Extra { get; set; }" : string.Empty;
            return AddAssemblyAttribute(BuildSource($$"""
[FakePackable]
public sealed class AdapterPayload
{
    public int Value { get; set; }
    {{extraMember}}
}

[SharpLink.Sdk.RpcContract]
public interface IAdapterIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<AdapterPayload> Echo(AdapterPayload value, CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1010101010101010UL, 0x2020202020202020UL)]
public sealed class StableAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "stable-adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
                "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(StableAdapter), \"stable-adapter/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");
        }

        var baseline = RunGeneratorAndGetSources(Source(includeExtraMember: false))
            .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var changed = RunGeneratorAndGetSources(Source(includeExtraMember: true))
            .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));

        Ensure(
            ExtractGeneratedCodecIdentity(baseline, "AdapterPayload") ==
            ExtractGeneratedCodecIdentity(changed, "AdapterPayload"),
            "SharpLink must not guess serializer-specific schema evolution for an opaque Adapter; the Adapter semantic identity must be bumped when the same target type changes wire schema");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterTargetsShouldHaveDistinctClosedCodecIdentity()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class AdapterPayloadA
{
    public int Value { get; set; }
}

[FakePackable]
public sealed class AdapterPayloadB
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IAdapterTargetContract : SharpLink.Sdk.IService
{
    ValueTask<AdapterPayloadA> EchoA(AdapterPayloadA value, CancellationToken cancellationToken);
    ValueTask<AdapterPayloadB> EchoB(AdapterPayloadB value, CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1010101010101010UL, 0x2020202020202020UL)]
public sealed class StableAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "stable-adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(StableAdapter), \"stable-adapter/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var manifest = RunGeneratorAndGetSources(source)
            .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        Ensure(
            ExtractGeneratedCodecIdentity(manifest, "AdapterPayloadA") !=
            ExtractGeneratedCodecIdentity(manifest, "AdapterPayloadB"),
            "one opaque Adapter must still produce distinct closed Codec identities for distinct stable target types");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtoStringFieldShouldUseInt32Utf16ContentFramingAndWireNull()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StringEnvelope
{
    public string? Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IDtoStringContract : SharpLink.Sdk.IService
{
    ValueTask<StringEnvelope> Echo(StringEnvelope value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(
            generated.Contains("GetByteCount(string value) => checked(value.Length * sizeof(char))", StringComparison.Ordinal),
            "generated DTO string fields must size UTF-16 code units rather than UTF-8 bytes");
        Ensure(
            generated.Contains("WriteInt32LittleEndian(length, byteCount)", StringComparison.Ordinal),
            "generated DTO string fields must use the v2 signed Int32 little-endian byte length");
        Ensure(
            generated.Contains("MemoryMarshal.Cast<byte, char>(payload)", StringComparison.Ordinal),
            "generated DTO string fields must write UTF-16 code units without UTF-8 transcoding");
        Ensure(
            generated.Contains("RpcGeneratedWireType.Null", StringComparison.Ordinal),
            "generated DTO string nulls must remain represented by the DTO field Null wire type rather than the root string -1 sentinel");
        return Task.CompletedTask;
    }
}
