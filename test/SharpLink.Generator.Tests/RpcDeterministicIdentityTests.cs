using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
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
            ExtractGeneratedCodecIdentity(first) == ExtractGeneratedCodecIdentity(second),
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
            ExtractGeneratedCodecIdentity(first) != ExtractGeneratedCodecIdentity(second),
            "changing opaque serializer semantics must change CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "changing a payload CodecHash must flow through MethodHash/ContractHash into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    private static string GenerateOpaqueIdentityManifest(
        string implementationMarker,
        ulong semanticHigh,
        ulong semanticLow)
    {
        var source = $$"""
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[RpcSerializable]
[RpcCodec(typeof(OpaquePayloadCodec))]
public sealed class OpaquePayload
{
    public int Value { get; set; }
}

[RpcCodecImplementation("opaque-test-wire/v1", "opaque-test-schema/v1")]
[RpcCodecSemanticIdentity({{semanticHigh}}UL, {{semanticLow}}UL)]
public sealed class OpaquePayloadCodec : IRpcCodec<OpaquePayload>
{
    private const string ImplementationMarker = "{{implementationMarker}}";

    public void Serialize(in OpaquePayload value, IBufferWriter<byte> buffer) { _ = ImplementationMarker; }
    public OpaquePayload Deserialize(in ReadOnlySequence<byte> buffer) => new();
}

[RpcContract]
public interface IOpaqueIdentityContract : IService
{
    ValueTask<OpaquePayload> Echo(OpaquePayload value, CancellationToken cancellationToken);
}
""";

        return RunGeneratorAndGetSources(source)
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }

    private static string ExtractGeneratedCodecIdentity(string manifest)
        => manifest.Split('\n')
            .Single(static line =>
                line.Contains(
                    "SharpLinkGeneratedCodecIdentityAttribute(typeof(global::OpaquePayload)",
                    StringComparison.Ordinal))
            .Trim();

    private static string ExtractGeneratedRpcAssemblyHash(string manifest)
        => manifest.Split('\n')
            .Single(static line => line.Contains("public RpcHash128 RpcAssemblyHash =>", StringComparison.Ordinal))
            .Trim();
}
