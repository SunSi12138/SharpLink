using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;
using SharpLink.MultiClusterTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public partial class RpcManifestCodecProviderTests
{
    [Test]
    public void GeneratedUnsafeBlitShouldResolveOnlyItsAdmittedFactoryWithoutCatalog()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [], [factory]));
        context.AdoptGeneratedManifest(registration);
        var provider = RpcGeneratedCodecResolver.GetProvider(registration);
        var generated = (IRpcGeneratedUnsafeBlitCodecProvider)provider;

        var direct = generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement);
        Ensure(direct is UnsafeBlitCodec<UncataloguedPayload>,
            "the exact admitted factory and ABI must authorize the generated UnsafeBlit codec");
        Ensure(factory.CreateCalls == 0,
            "the capability guard must not invoke its factory while validating provenance");
        Ensure(ReferenceEquals(provider.GetCodec<UncataloguedPayload>(), direct),
            "normal owner resolution must reach the same generated UnsafeBlit codec");
        Ensure(factory.CreateCalls == 1,
            "normal resolution must invoke the admitted factory exactly once without guard recursion");
        Ensure(ReferenceEquals(provider.GetCodec<UncataloguedPayload>(), direct) && factory.CreateCalls == 1,
            "the normal owner provider must preserve its resolved codec cache");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectUnregisteredUnmanagedTypeWithValidAbi()
    {
        var resolverCalls = 0;
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseCodecResolver(_ =>
            {
                resolverCalls++;
                return UnsafeBlitCodec<UncataloguedPayload>.Instance;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [], []));
        var generated = GetUnsafeBlitProvider(registration);

        ExpectUnadmittedUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, new(8, false)),
            "a plausible ABI must not authorize an unmanaged type missing from the owner's generated graph");
        Ensure(factory.CreateCalls == 0 && resolverCalls == 0,
            "a missing registration must fail without invoking a supplied factory or standalone resolver");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectRegisteredFactoryForAnotherGenericType()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration).GetGeneratedUnsafeBlitCodec<OtherUncataloguedPayload>(
                factory, factory.Requirement),
            "an admitted factory must not authorize a different generic target with the same ABI");
        Ensure(factory.CreateCalls == 0, "generic target validation must not invoke the factory");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectForeignFactoryWithMatchingTypeAndAbi()
    {
        var admitted = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        var foreign = new UnsafeBlitFactory<UncataloguedPayload>(admitted.Requirement);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [admitted], []));

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration).GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(
                foreign, admitted.Requirement),
            "matching type, CodecHash, and ABI must not substitute for factory reference identity");
        Ensure(admitted.CreateCalls == 0 && foreign.CreateCalls == 0,
            "a foreign factory must be rejected without running either factory");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectFactoryFromAnotherOwner()
    {
        var factoryA = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        var factoryB = new UnsafeBlitFactory<UncataloguedPayload>(factoryA.Requirement);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var ownerA = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factoryA], []));
        using var ownerB = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IOrdersContract).Assembly, [factoryB], []));
        var providerA = GetUnsafeBlitProvider(ownerA);
        var providerB = GetUnsafeBlitProvider(ownerB);

        ExpectUnadmittedUnsafeBlit(
            () => providerA.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factoryB, factoryB.Requirement),
            "owner A must not accept owner B's factory for the same CLR type and ABI");
        ExpectUnadmittedUnsafeBlit(
            () => providerB.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factoryA, factoryA.Requirement),
            "owner B must not accept owner A's factory for the same CLR type and ABI");
        Ensure(providerA.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factoryA, factoryA.Requirement) is not null &&
               providerB.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factoryB, factoryB.Requirement) is not null,
            "each live owner must retain its own independently admitted capability");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectFactoryFromAnotherRegistrationGeneration()
    {
        var previous = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        var incoming = new UnsafeBlitFactory<UncataloguedPayload>(previous.Requirement);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var oldRegistration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [previous], []));
        var newRegistration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [incoming], []));
        context.AdoptGeneratedManifest(oldRegistration);
        context.AdoptGeneratedManifest(newRegistration);
        var oldProvider = GetUnsafeBlitProvider(oldRegistration);
        var newProvider = (IRpcGeneratedUnsafeBlitCodecProvider)RpcGeneratedCodecResolver.GetProvider(
            context, typeof(IContractA).Assembly);

        ExpectUnadmittedUnsafeBlit(
            () => oldProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(incoming, incoming.Requirement),
            "a retained provider must not accept an incoming generation's factory");
        ExpectUnadmittedUnsafeBlit(
            () => newProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(previous, previous.Requirement),
            "the incoming generation must not accept an earlier factory from the same assembly");
        context.ReleaseGeneratedManifest(oldRegistration);
        Ensure(newProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(incoming, incoming.Requirement) is not null,
            "releasing an earlier generation must not revoke the incoming owner's capability");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectForgedPointerWidthBeforePlatformValidation()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(4, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));
        var generated = GetUnsafeBlitProvider(registration);

        ExpectUnadmittedUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, new(8, false)),
            "a caller must not replace an admitted unsupported pointer ABI with a supported one");
        ExpectMissingUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement),
            "an exact admitted requirement must still undergo the platform ABI guard");
        Ensure(factory.CreateCalls == 0, "neither provenance nor platform validation may invoke the factory");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectForgedDateTimeOffsetRequirement()
    {
        var factory = new UnsafeBlitFactory<DateTimeOffsetPayload>(new(8, true));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration).GetGeneratedUnsafeBlitCodec<DateTimeOffsetPayload>(
                factory, new(8, false)),
            "a caller must not suppress the owner's captured DateTimeOffset raw ABI requirement");
        Ensure(factory.CreateCalls == 0, "rejecting a forged ABI flag must not invoke the factory");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectMissingRequirementForAdmittedFactory()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration).GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, default),
            "an admitted factory still requires the exact captured nondefault ABI argument");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldNotReplaceCustomCodecBindingForSameType()
    {
        var codec = new CustomUnsafeBlitPayloadCodec();
        var foreign = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [new NativeFactory<UncataloguedPayload>(_ => codec)], []));
        var provider = RpcGeneratedCodecResolver.GetProvider(registration);

        ExpectUnadmittedUnsafeBlit(
            () => ((IRpcGeneratedUnsafeBlitCodecProvider)provider).GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(
                foreign, foreign.Requirement),
            "a custom-codec-bound unmanaged type must not gain an unregistered UnsafeBlit capability");
        Ensure(ReferenceEquals(provider.GetCodec<UncataloguedPayload>(), codec),
            "the custom binding must remain the owner's normal codec after a denied capability request");
    }

    [Test]
    public void GeneratedUnsafeBlitContractCustomBindingShouldShadowGlobalCapability()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        var contractCodec = new CustomUnsafeBlitPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], [new NativeFactory<UncataloguedPayload>(_ => contractCodec)]));
        var contractProvider = RpcGeneratedCodecResolver.GetProvider(registration);
        var globalProvider = RpcGeneratedCodecResolver.GetProvider(registration, RpcGeneratedCodecResolutionScope.Global);

        ExpectUnadmittedUnsafeBlit(
            () => ((IRpcGeneratedUnsafeBlitCodecProvider)contractProvider).GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(
                factory, factory.Requirement),
            "a Contract custom binding must block the shadowed global UnsafeBlit factory");
        Ensure(ReferenceEquals(contractProvider.GetCodec<UncataloguedPayload>(), contractCodec),
            "normal Contract resolution must retain its custom codec binding");
        Ensure(((IRpcGeneratedUnsafeBlitCodecProvider)globalProvider)
                   .GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement) is not null,
            "the same global factory must remain authorized in the owner's global scope");
        Ensure(globalProvider.GetCodec<UncataloguedPayload>() is UnsafeBlitCodec<UncataloguedPayload>,
            "normal global resolution must not inherit the Contract-only custom policy");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldKeepContractAndGlobalFactoriesInTheirExactScopes()
    {
        var globalFactory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        var contractFactory = new UnsafeBlitFactory<UncataloguedPayload>(globalFactory.Requirement);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [globalFactory], [contractFactory]));
        var contractProvider = GetUnsafeBlitProvider(registration);
        var globalProvider = GetUnsafeBlitProvider(registration, RpcGeneratedCodecResolutionScope.Global);

        ExpectUnadmittedUnsafeBlit(
            () => contractProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(globalFactory, globalFactory.Requirement),
            "Contract scope must reject a shadowed global factory even when both are UnsafeBlit factories");
        ExpectUnadmittedUnsafeBlit(
            () => globalProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(contractFactory, contractFactory.Requirement),
            "global scope must reject the Contract-only factory for the same target and ABI");
        Ensure(contractProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(contractFactory, contractFactory.Requirement) is not null &&
               globalProvider.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(globalFactory, globalFactory.Requirement) is not null,
            "each scope must accept only its selected factory registration");
    }

    [Test]
    public void GeneratedUnsafeBlitGlobalScopeShouldRejectContractOnlyCapability()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [], [factory]));

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration, RpcGeneratedCodecResolutionScope.Global)
                .GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement),
            "a Contract-only capability must remain unavailable in the owner's global graph");
        Ensure(GetUnsafeBlitProvider(registration)
                   .GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement) is not null,
            "the Contract-only registration must still authorize its Contract scope");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldUseCapturedAbiAfterFactoryMetadataMutation()
    {
        var captured = new SharpLinkGeneratedUnsafeBlitRequirement(8, false);
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(captured);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));
        var generated = GetUnsafeBlitProvider(registration);
        factory.Requirement = new(4, true);

        ExpectUnadmittedUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement),
            "mutating live factory metadata must not replace the ABI captured at preparation");
        Ensure(generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, captured) is not null,
            "the captured ABI must remain authoritative after factory metadata changes");
        Ensure(factory.CreateCalls == 0, "snapshot validation must not instantiate the mutable factory");
    }

    [Test]
    public void GeneratedUnsafeBlitMetadataMutationShouldNotSuppressCapturedPlatformRequirements()
    {
        var factory = new UnsafeBlitFactory<DateTimeOffsetPayload>(new(8, true));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));
        factory.Requirement = new(8, false);

        ExpectUnadmittedUnsafeBlit(
            () => GetUnsafeBlitProvider(registration).GetGeneratedUnsafeBlitCodec<DateTimeOffsetPayload>(
                factory, factory.Requirement),
            "changing the factory's ABI flag after preparation must not suppress the captured DateTimeOffset guard");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldUseCapturedTargetAfterFactoryMetadataMutation()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));
        var generated = GetUnsafeBlitProvider(registration);
        factory.TargetType = typeof(OtherUncataloguedPayload);

        ExpectUnadmittedUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<OtherUncataloguedPayload>(factory, factory.Requirement),
            "changing the factory's live target must not authorize another generic type");
        Ensure(generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement) is not null,
            "the prepared target must remain authoritative after mutable factory metadata changes");
    }

    [Test]
    public void GeneratedUnsafeBlitShouldRejectDisposedOwnerEvenAfterSuccessfulResolution()
    {
        var factory = new UnsafeBlitFactory<UncataloguedPayload>(new(8, false));
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new UnsafeBlitManifest(
            typeof(IContractA).Assembly, [factory], []));
        context.AdoptGeneratedManifest(registration);
        var provider = RpcGeneratedCodecResolver.GetProvider(registration);
        var generated = (IRpcGeneratedUnsafeBlitCodecProvider)provider;
        _ = provider.GetCodec<UncataloguedPayload>();
        _ = generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement);
        context.ReleaseGeneratedManifest(registration);

        ExpectDisposedUnsafeBlit(
            () => generated.GetGeneratedUnsafeBlitCodec<UncataloguedPayload>(factory, factory.Requirement),
            "a released owner must revoke an already-used generated capability");
        ExpectDisposedUnsafeBlit(
            () => provider.GetCodec<UncataloguedPayload>(),
            "the owner's cached normal resolution must also reject use after release");
        Ensure(factory.CreateCalls == 1, "disposal rejection must not invoke the factory again");
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

    private static IRpcGeneratedUnsafeBlitCodecProvider GetUnsafeBlitProvider(
        RpcGeneratedManifestRegistration registration,
        RpcGeneratedCodecResolutionScope scope = RpcGeneratedCodecResolutionScope.Contract)
        => (IRpcGeneratedUnsafeBlitCodecProvider)RpcGeneratedCodecResolver.GetProvider(registration, scope);

    private static void ExpectUnadmittedUnsafeBlit(Action action, string message)
    {
        try { action(); }
        catch (PlatformNotSupportedException) { return; }
        throw new Exception(message);
    }

    private static void ExpectDisposedUnsafeBlit(Action action, string message)
    {
        try { action(); }
        catch (ObjectDisposedException) { return; }
        throw new Exception(message);
    }

    private static void ExpectMissingUnsafeBlit(Action action, string message)
    {
        try { action(); }
        catch (PlatformNotSupportedException) { return; }
        throw new Exception(message);
    }

    private struct UncataloguedPayload
    {
        public long Value { get; set; }
    }

    private struct OtherUncataloguedPayload
    {
        public long Value { get; set; }
    }

    private struct DateTimeOffsetPayload
    {
        public DateTimeOffset Value { get; set; }
    }

    private sealed class CustomUnsafeBlitPayloadCodec : IRpcCodec<UncataloguedPayload>
    {
        public void Serialize(in UncataloguedPayload value, IBufferWriter<byte> buffer) { }
        public UncataloguedPayload Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class UnsafeBlitFactory<T>(SharpLinkGeneratedUnsafeBlitRequirement requirement)
        : ITestGeneratedCodecFactory, IRpcGeneratedUnsafeBlitCodecFactory
    {
        public Type TargetType { get; set; } = typeof(T);
        public SharpLinkGeneratedUnsafeBlitRequirement Requirement { get; set; } = requirement;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        internal int CreateCalls { get; private set; }

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            Ensure(adapterScope is null, "an UnsafeBlit factory must not receive an adapter scope");
            CreateCalls++;
            return ((IRpcGeneratedUnsafeBlitCodecProvider)provider).GetGeneratedUnsafeBlitCodec<T>(this, Requirement);
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class UnsafeBlitManifest(
        Assembly ownerAssembly,
        IReadOnlyList<IRpcGeneratedCodecFactory> codecs,
        IReadOnlyList<IRpcGeneratedCodecFactory> contractCodecs) : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "unsafe-blit-provenance-test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "unsafe-blit-provenance-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } = contractCodecs;
        public IReadOnlyList<string> Dependencies => [];
    }
}
