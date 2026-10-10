using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public partial class RpcManifestCodecProviderTests
{
    [Test]
    public void GeneratedUnsafeBlitShouldValidateExplicitRequirementWithoutCatalog()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new ContractCodecManifest(
            typeof(IContractA).Assembly, new NamedContractCodec("generated-abi"), "generated-abi"));
        context.AdoptGeneratedManifest(registration);
        var provider = RpcGeneratedCodecResolver.GetProvider(registration);
        var generated = (IRpcGeneratedUnsafeBlitCodecProvider)provider;
        Ensure(generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(new(8, false)) is not null,
            "an explicit valid generated requirement must construct its codec");
        try
        {
            _ = generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(default);
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        throw new Exception("A missing generated ABI requirement must fail closed.");
    }

    [Test]
    public void GeneratedUnsafeBlitMissingFactoryMustNotConsultStandaloneResolver()
    {
        var resolverCalls = 0;
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseCodecResolver(type =>
            {
                resolverCalls++;
                return type == typeof(UncataloguedPayload) ? UnsafeBlitCodec<UncataloguedPayload>.Instance : null;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new ContractCodecManifest(
            typeof(IContractA).Assembly, new NamedContractCodec("missing-abi"), "missing-abi"));
        context.AdoptGeneratedManifest(registration);
        ExpectMissingUnsafeBlit(
            () => RpcGeneratedCodecResolver.GetProvider(registration).GetCodec<UncataloguedPayload>(),
            "A generated owner must never use the standalone resolver for a missing factory.");
        Ensure(resolverCalls == 0, "owner-local failure must not invoke the supplied standalone resolver");
        Ensure(context.Codecs.GetCodec<UncataloguedPayload>() is not null && resolverCalls == 1,
            "the explicit standalone resolver must remain usable outside the generated graph");
    }

    private static void ExpectMissingUnsafeBlit(Action action, string message)
    {
        try
        {
            action();
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        throw new Exception(message);
    }

    private struct UncataloguedPayload
    {
        public long Value { get; set; }
    }
}
