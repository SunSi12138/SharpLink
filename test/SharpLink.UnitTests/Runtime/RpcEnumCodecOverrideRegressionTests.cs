using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class RpcEnumCodecOverrideRegressionTests
{
    [Test]
    public void AssemblyOwnedEnumShouldIgnoreExplicitRuntimeCodecOverride()
    {
        using var defaultContext = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        Ensure(ReferenceEquals(defaultContext.Codecs.GetCodec<TestMode>(), EnumCodec<TestMode>.Instance),
            "enum should use the deterministic shared native Codec when no explicit runtime Codec is configured");

        var explicitCodec = new TestModeCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<TestMode>(explicitCodec)
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new AssemblyEnumManifest();
        var registration = context.PrepareGeneratedManifest(manifest);
        context.AdoptGeneratedManifest(registration);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<TestMode>(), explicitCodec),
            "the context-global provider may retain its explicit runtime Codec for non-RPC consumers");
        var contractProvider = RpcGeneratedCodecResolver.GetProvider(context, manifest.OwnerAssembly);
        Ensure(ReferenceEquals(contractProvider.GetCodec<TestMode>(), EnumCodec<TestMode>.Instance),
            "the Contract assembly provider must ignore endpoint runtime overrides and keep deterministic RPC enum semantics");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private enum TestMode : int
    {
        None,
        Active
    }

    private sealed class TestModeCodec : IRpcCodec<TestMode>
    {
        public void Serialize(in TestMode value, IBufferWriter<byte> writer)
        {
        }

        public TestMode Deserialize(in ReadOnlySequence<byte> buffer) => TestMode.Active;
    }

    private sealed class AssemblyEnumManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "enum-runtime-override-regression";
        public Assembly OwnerAssembly => typeof(RpcEnumCodecOverrideRegressionTests).Assembly;
        public string CompileTimeDescriptor => "enum-runtime-override-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
