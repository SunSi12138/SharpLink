using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;
using SharpLink.MultiClusterTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcManifestCodecOwnershipRegressionTests
{
    [Test]
    public void PolicyOwnerShouldFreezeUnroutedUnmanagedCodecAgainstRuntimeOverride()
    {
        var runtimeValue = new RuntimeUnroutedValueCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(runtimeValue)
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new PolicyManifest(
            typeof(IContractA).Assembly,
            new PolicyPointCodec());
        var registration = context.PrepareGeneratedManifest(manifest);
        context.AdoptGeneratedManifest(registration);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<UnroutedValue>(), runtimeValue),
            "the context-global provider must retain the explicit runtime Codec for the unmanaged value");
        var ownerValue = RpcGeneratedCodecResolver.GetProvider(context, manifest.OwnerAssembly)
            .GetCodec<UnroutedValue>();
        Ensure(!ReferenceEquals(ownerValue, runtimeValue),
            "once a Contract owner has compile-time policy, its unrouted unmanaged remainder must come from the frozen compile-time graph rather than runtime UseCodec state");
        Ensure(ownerValue.GetType().Name.Contains("UnsafeBlitCodec", StringComparison.Ordinal),
            "the policy owner must resolve the deterministic compile-time unmanaged fallback independently of endpoint runtime overrides");
    }

    [Test]
    public void NoPolicyOwnerShouldNotBorrowEquivalentGeneratedAdapterFromAnotherModule()
    {
        var scopeA = new SharedScopeState("A");
        var scopeB = new SharedScopeState("B");
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifestA = new SharedAdapterManifest(
            typeof(IContractA).Assembly,
            new SharedAdapter(scopeA));
        var manifestB = new SharedAdapterManifest(
            typeof(IOrdersContract).Assembly,
            new SharedAdapter(scopeB));

        var registrationA = context.PrepareGeneratedManifest(manifestA);
        context.PublishGeneratedCodecs(registrationA.Codecs);
        context.AdoptGeneratedManifest(registrationA);
        var publishedA = context.Codecs.GetCodec<SharedValue>() as SharedValueCodec;
        Ensure(publishedA is not null && publishedA.Owner == "A",
            "the setup must publish module A's generated Adapter Codec globally first");

        var registrationB = context.PrepareGeneratedManifest(manifestB);
        var providerB = RpcGeneratedCodecResolver.GetProvider(registrationB);
        var codecB = providerB.GetCodec<SharedValue>() as SharedValueCodec;
        Ensure(codecB is not null && codecB.Owner == "B",
            "a no-policy incoming owner must instantiate its own equivalent generated Adapter instead of borrowing module A's published instance");

        context.PublishGeneratedCodecs(registrationB.Codecs);
        context.AdoptGeneratedManifest(registrationB);
        context.ReleaseGeneratedManifest(registrationA);

        Ensure(scopeA.Disposed,
            "releasing module A must dispose A's Adapter scope so the regression exercises the lifetime boundary");
        Ensure(!scopeB.Disposed && codecB!.Owner == "B",
            "module B's already-bound Codec must remain backed by B's live scope after the equivalent module A generation is released");
    }

    [Test]
    public void PreAdoptionNoPolicyProviderShouldExposeIncomingGeneratedCodec()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new IncomingGeneratedManifest(
            typeof(IContractA).Assembly);
        using var registration = context.PrepareGeneratedManifest(manifest);

        var provider = RpcGeneratedCodecResolver.GetProvider(registration);
        Ensure(provider.GetCodec<IncomingValue>() is IncomingValueCodec,
            "dynamic pre-adoption binding must see the incoming manifest's ordinary generated Codecs before they are published into the context snapshot");
        try
        {
            _ = context.Codecs.GetCodec<IncomingValue>();
            throw new InvalidOperationException(
                "the setup requires the incoming generated Codec to still be absent from the context-global snapshot");
        }
        catch (NotSupportedException)
        {
        }
    }

    private readonly record struct PolicyPoint(int X, int Y);
    private readonly record struct UnroutedValue(int Value);
    private readonly record struct SharedValue(int Value);
    private sealed class IncomingValue { }

    private sealed class RuntimeUnroutedValueCodec : IRpcCodec<UnroutedValue>
    {
        public void Serialize(in UnroutedValue value, IBufferWriter<byte> buffer) { }
        public UnroutedValue Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class PolicyPointCodec : IRpcCodec<PolicyPoint>
    {
        public void Serialize(in PolicyPoint value, IBufferWriter<byte> buffer) { }
        public PolicyPoint Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class IncomingValueCodec : IRpcCodec<IncomingValue>
    {
        public void Serialize(in IncomingValue value, IBufferWriter<byte> buffer) { }
        public IncomingValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class SharedScopeState(string owner)
    {
        internal string Owner { get; } = owner;
        internal bool Disposed { get; set; }
    }

    private sealed class SharedValueCodec(SharedScopeState state) : IRpcCodec<SharedValue>
    {
        internal string Owner
        {
            get
            {
                if (state.Disposed)
                    throw new ObjectDisposedException("shared Adapter scope " + state.Owner);
                return state.Owner;
            }
        }

        public void Serialize(in SharedValue value, IBufferWriter<byte> buffer)
        {
            _ = Owner;
        }

        public SharedValue Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = Owner;
            return default;
        }
    }

    private sealed class SharedAdapter(SharedScopeState state) : IRpcCodecAdapter
    {
        public string AdapterId => "shared-owner-lifetime/v1";
        public string WireFormatId => "shared-owner-wire/v1";
        public IRpcCodecAdapterScope CreateScope() => new SharedAdapterScope(state);
    }

    private sealed class SharedAdapterScope(SharedScopeState state) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
            => typeof(T) == typeof(SharedValue)
                ? (IRpcCodec<T>)(object)new SharedValueCodec(state)
                : throw new NotSupportedException($"Unexpected shared owner target '{typeof(T)}'.");

        public void Dispose() => state.Disposed = true;
    }

    private sealed class NativeFactory<T>(Func<IRpcCodecProvider, IRpcCodec<T>> create, string schemaId)
        : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public string SchemaId { get; } = schemaId;
        public string WireFormatId => "sharplink-native/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            if (adapterScope is not null)
                throw new ArgumentException("native regression factory does not accept an Adapter scope", nameof(adapterScope));
            return create(provider);
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class SharedAdapterFactory(SharedAdapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(SharedValue);
        public string SchemaId => "shared-owner-schema/v1";
        public string WireFormatId => adapter.WireFormatId;
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<SharedValue>() ?? throw new ArgumentNullException(nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<SharedValue>;
    }

    private sealed class PolicyManifest(Assembly ownerAssembly, PolicyPointCodec codec)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "policy-owner-freeze-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "policy-owner-freeze-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } =
            [new NativeFactory<PolicyPoint>(_ => codec, "policy-point/v1")];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class SharedAdapterManifest(Assembly ownerAssembly, SharedAdapter adapter)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "shared-owner-lifetime-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "shared-owner-lifetime-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new SharedAdapterFactory(adapter)];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class IncomingGeneratedManifest(Assembly ownerAssembly)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "incoming-generated-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "incoming-generated-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
            [new NativeFactory<IncomingValue>(static _ => new IncomingValueCodec(), "incoming-generated/v1")];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
