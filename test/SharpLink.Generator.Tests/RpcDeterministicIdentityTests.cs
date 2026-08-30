using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task OpaqueSemanticIdentityShouldIgnoreLegacyStringChanges()
    {
        var first = GenerateOpaqueIdentityManifest(
            wireFormatId: "legacy-wire-a/v1",
            schemaId: "legacy-schema-a/v1",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);
        var second = GenerateOpaqueIdentityManifest(
            wireFormatId: "legacy-wire-b/v9",
            schemaId: "legacy-schema-b/v9",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);

        Ensure(
            ExtractGeneratedCodecIdentity(first) == ExtractGeneratedCodecIdentity(second),
            "fixed opaque semantic identity must replace legacy WireFormatId/SchemaId as the CodecHash input");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) == ExtractGeneratedRpcAssemblyHash(second),
            "legacy custom-codec strings must not perturb RpcAssemblyHash once fixed semantic identity is present");
        return Task.CompletedTask;
    }

    [Test]
    public Task OpaqueSemanticIdentityChangeShouldChangeFinalRpcIdentity()
    {
        var first = GenerateOpaqueIdentityManifest(
            wireFormatId: "same-wire/v1",
            schemaId: "same-schema/v1",
            semanticHigh: 0x0102030405060708UL,
            semanticLow: 0x1112131415161718UL);
        var second = GenerateOpaqueIdentityManifest(
            wireFormatId: "same-wire/v1",
            schemaId: "same-schema/v1",
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
        string wireFormatId,
        string schemaId,
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

[RpcCodecImplementation("{{wireFormatId}}", "{{schemaId}}")]
[RpcCodecSemanticIdentity({{semanticHigh}}UL, {{semanticLow}}UL)]
public sealed class OpaquePayloadCodec : IRpcCodec<OpaquePayload>
{
    public void Serialize(in OpaquePayload value, IBufferWriter<byte> buffer) { }
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
