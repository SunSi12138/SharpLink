using System.Buffers;
using System.Linq;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public partial class RpcManifestCodecProviderTests
{
    [Test]
    public void CompiledGeneratedManifestShouldAdmitExactRawFactoryAndRoundTrip()
    {
        var assembly = typeof(IGeneratedRawOwnerContract).Assembly;
        var result = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        Ensure(result.Succeeded && manifest is not null, $"compiled generated manifest must load: {result.Error}");
        var factory = manifest!.Codecs.Single(item => item.TargetType == typeof(GeneratedOwnerRawPayload));
        Ensure(factory is IRpcGeneratedUnsafeBlitCodecFactory,
            "the actual generated manifest must admit its raw factory without an identity-only decorator");
        var identity = assembly.GetCustomAttributes<SharpLinkGeneratedCodecIdentityAttribute>()
            .Single(item => item.TargetType == typeof(GeneratedOwnerRawPayload));
        Ensure(!identity.CodecHash.IsEmpty && factory.CodecHash == identity.CodecHash,
            "direct raw registration must retain the exact finalized CodecHash published to consumers");

        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(manifest);
        context.PublishGeneratedCodecs(registration.Codecs, registration);
        context.AdoptGeneratedManifest(registration);
        RoundTripGeneratedRaw(context.Codecs.GetCodec<GeneratedOwnerRawPayload>());
        RoundTripGeneratedRaw(RpcGeneratedCodecResolver.GetProvider(registration).GetCodec<GeneratedOwnerRawPayload>());
    }

    private static void RoundTripGeneratedRaw(IRpcCodec<GeneratedOwnerRawPayload> codec)
    {
        var expected = new GeneratedOwnerRawPayload { Stamp = 0x0102030405060708, Count = 42 };
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in expected, writer);
        var bytes = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var actual = codec.Deserialize(in bytes);
        Ensure(actual.Stamp == expected.Stamp && actual.Count == expected.Count,
            "the actual generated owner factory must produce a working raw codec");
    }
}
