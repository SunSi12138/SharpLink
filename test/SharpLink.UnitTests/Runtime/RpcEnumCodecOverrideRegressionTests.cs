using System.Buffers;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class RpcEnumCodecOverrideRegressionTests
{
    [Test]
    public void NoPolicyEnumShouldKeepExplicitRuntimeCodecPrecedence()
    {
        using var defaultContext = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        Ensure(ReferenceEquals(defaultContext.Codecs.GetCodec<TestMode>(), EnumCodec<TestMode>.Instance),
            "enum should use the deterministic shared native Codec when no explicit runtime Codec is configured");

        var explicitCodec = new TestModeCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec<TestMode>(explicitCodec)
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new NoPolicyEnumManifest());
        context.AdoptGeneratedManifest(registration);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<TestMode>(), explicitCodec),
            "explicit runtime enum Codec should override the shared native default");
        var contractProvider = RpcGeneratedCodecResolver.GetProvider(context, typeof(NoPolicyContract));
        Ensure(ReferenceEquals(contractProvider.GetCodec<TestMode>(), explicitCodec),
            "a no-policy Contract should preserve explicit runtime enum Codec precedence");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private interface NoPolicyContract
    {
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

    private sealed class NoPolicyEnumManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "enum-runtime-override-regression";
        public Assembly OwnerAssembly => typeof(RpcEnumCodecOverrideRegressionTests).Assembly;
        public string CompileTimeDescriptor => "enum-runtime-override-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<SharpLinkGeneratedContractCodecSet> ContractCodecSets =>
            [new(typeof(NoPolicyContract), HasCompileTimePolicy: false, Codecs: [], Dependencies: [])];
        public IReadOnlyList<string> Dependencies => [];
    }
}
