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
    public Task AdapterOwnedWireVisibleMemberChangeShouldChangeClosedCodecIdentity()
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
            ExtractGeneratedCodecIdentity(baseline, "AdapterPayload") !=
            ExtractGeneratedCodecIdentity(changed, "AdapterPayload"),
            "wire-visible target schema changes must change the closed Adapter CodecHash even when Adapter identity is unchanged");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(baseline) != ExtractGeneratedRpcAssemblyHash(changed),
            "closed Adapter target schema changes must propagate into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    [Test]
    public Task RootStringCodecIdentityShouldNotReuseDtoUtf8LeafIdentity()
    {
        var rootStringSource = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IRootStringContract : SharpLink.Sdk.IService
{
    ValueTask<string> Echo(string value, CancellationToken cancellationToken);
}
""");
        var dtoStringSource = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StringEnvelope
{
    public string Value { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface IDtoStringContract : SharpLink.Sdk.IService
{
    ValueTask<StringEnvelope> Echo(StringEnvelope value, CancellationToken cancellationToken);
}
""");

        var rootManifest = RunGeneratorAndGetSources(rootStringSource)
            .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var dtoManifest = RunGeneratorAndGetSources(dtoStringSource)
            .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));

        var rootStringIdentity = rootManifest.Split('\n')
            .Single(static line =>
                line.Contains("SharpLinkGeneratedCodecIdentityAttribute", StringComparison.Ordinal) &&
                (line.Contains("typeof(string)", StringComparison.Ordinal) ||
                 line.Contains("typeof(global::System.String)", StringComparison.Ordinal)))
            .Trim();
        var dtoIdentity = ExtractGeneratedCodecIdentity(dtoManifest, "StringEnvelope");

        Ensure(!string.IsNullOrWhiteSpace(rootStringIdentity),
            "root string must publish the framework StringCodec identity");
        Ensure(rootStringIdentity != dtoIdentity,
            "root StringCodec identity and generated DTO UTF-8 string-field semantics must remain distinct");
        return Task.CompletedTask;
    }
}
