using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;
using SharpLink.MultiClusterTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public class RpcManifestCodecProviderTests
{
    [Test]
    public void ManifestScopedRouteShouldFlowIntoNativeDtoAndCollectionDependencies()
    {
        var routedPoint = new RoutedPointCodec();
        var ownerAssembly = typeof(IContractA).Assembly;
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(
            new NestedRouteManifest(ownerAssembly, routedPoint));
        context.AdoptGeneratedManifest(registration);

        var ownerProvider = RpcGeneratedCodecResolver.GetProvider(context, ownerAssembly);
        var envelopeCodec = ownerProvider.GetCodec<Envelope>() as EnvelopeCodec;
        var listCodec = ownerProvider.GetCodec<List<Point>>() as PointListCodec;

        Ensure(envelopeCodec is not null && ReferenceEquals(envelopeCodec.PointCodec, routedPoint),
            "a native DTO must resolve a routed nested member through its Contract owner provider");
        Ensure(listCodec is not null && ReferenceEquals(listCodec.ElementCodec, routedPoint),
            "a native collection must resolve a routed nested element through its Contract owner provider");
        Ensure(!ReferenceEquals(context.Codecs.GetCodec<Point>(), routedPoint),
            "the routed unmanaged Point Codec must remain absent from the context-global provider");
    }

    [Test]
    public void NoRouteOwnerBindingShouldIgnoreRuntimeContextCodecRegistration()
    {
        var explicitCodec = new ExplicitNoRouteCodec();
        var ownerAssembly = typeof(IContractA).Assembly;
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(explicitCodec)
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new NoRouteManifest(ownerAssembly));
        context.AdoptGeneratedManifest(registration);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<NoRouteValue>(), explicitCodec),
            "the context-global provider must retain the explicit runtime Codec for non-generated consumers");

        var staticProvider = RpcGeneratedCodecResolver.GetProvider(context, ownerAssembly);
        var dynamicCandidateProvider = RpcGeneratedCodecResolver.GetProvider(registration);
        var staticCodec = staticProvider.GetCodec<NoRouteValue>();
        var dynamicCodec = dynamicCandidateProvider.GetCodec<NoRouteValue>();

        Ensure(staticCodec is GeneratedNoRouteCodec && dynamicCodec is GeneratedNoRouteCodec,
            "both adopted and candidate owner providers must resolve the manifest-generated no-route Codec");
        Ensure(ReferenceEquals(staticCodec, dynamicCodec),
            "static and dynamic owner resolution must share one frozen assembly-owned Codec binding");
        Ensure(!ReferenceEquals(staticCodec, explicitCodec),
            "runtime context Codec registration must not override generated RPC owner semantics");
    }

    [Test]
    public void ContractOwnedCodecBindingsShouldCoexistForSameClrType()
    {
        var ownerA = typeof(IContractA).Assembly;
        var ownerB = typeof(IOrdersContract).Assembly;
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var codecA = new NamedContractCodec("A");
        var codecB = new NamedContractCodec("B");
        var registrationA = context.PrepareGeneratedManifest(new ContractCodecManifest(ownerA, codecA, "contract-a"));
        var registrationB = context.PrepareGeneratedManifest(new ContractCodecManifest(ownerB, codecB, "contract-b"));
        context.AdoptGeneratedManifest(registrationA);
        context.AdoptGeneratedManifest(registrationB);

        Ensure(ReferenceEquals(RpcGeneratedCodecResolver.GetProvider(context, ownerA).GetCodec<ContractValue>(), codecA),
            "Contract owner A must resolve its own binding for the shared CLR type");
        Ensure(ReferenceEquals(RpcGeneratedCodecResolver.GetProvider(context, ownerB).GetCodec<ContractValue>(), codecB),
            "Contract owner B must resolve its own binding for the shared CLR type");
        var global = context.Codecs.GetCodec<ContractValue>();
        Ensure(!ReferenceEquals(global, codecA) && !ReferenceEquals(global, codecB),
            "Contract-owned bindings must never be published to the global Type -> Codec registry");
    }

    [Test]
    public void ManifestScopedProviderShouldApplyUnsafeBlitPlatformGuard()
    {
        var ownerAssembly = typeof(IContractA).Assembly;
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(
            new ContractCodecManifest(ownerAssembly, new NamedContractCodec("guard"), "unsafe-blit-guard"));
        context.AdoptGeneratedManifest(registration);
        var ownerProvider = RpcGeneratedCodecResolver.GetProvider(context, ownerAssembly);

        try
        {
            _ = ownerProvider.GetCodec<System.Numerics.Vector<int>>();
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        throw new Exception("Contract-scoped Codec resolution must apply the UnsafeBlit platform guard.");
    }

    [Test]
    public void CustomRuntimeMustExposeContractCodecResolution()
    {
        try
        {
            _ = RpcGeneratedCodecResolver.GetProvider(
                new CustomRuntimeContext(),
                typeof(RpcManifestCodecProviderTests));
        }
        catch (NotSupportedException exception)
        {
            Ensure(exception.Message.Contains(nameof(IRpcContractCodecProviderResolver), StringComparison.Ordinal),
                "custom runtimes must receive a deterministic owner-resolution requirement");
            return;
        }

        throw new Exception("Expected a custom IRpcRuntimeContext without owner resolution to be rejected.");
    }

    [Test]
    public void RuntimeContextBuildShouldRejectPreviousGeneratedManifestApi()
    {
        try
        {
            using var _ = new SharpLinkRuntimeContextBuilder().Build(
                [new PreviousApiManifest(typeof(IContractA).Assembly)]);
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("incompatible", StringComparison.OrdinalIgnoreCase),
                "direct RuntimeContext construction must fail at the generated-manifest compatibility boundary");
            return;
        }

        throw new Exception("Expected a previous generated manifest API to be rejected by RuntimeContext construction.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Point(int X, int Y);
    private readonly record struct NoRouteValue(int Value);
    private readonly record struct ContractValue(int Value);

    private sealed class Envelope
    {
        public Point Point { get; init; }
    }

    private sealed class RoutedPointCodec : IRpcCodec<Point>
    {
        public void Serialize(in Point value, IBufferWriter<byte> buffer)
        {
        }

        public Point Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class EnvelopeCodec : IRpcCodec<Envelope>
    {
        internal EnvelopeCodec(IRpcCodecProvider provider)
        {
            PointCodec = provider.GetCodec<Point>();
        }

        internal IRpcCodec<Point> PointCodec { get; }

        public void Serialize(in Envelope value, IBufferWriter<byte> buffer)
        {
        }

        public Envelope? Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class PointListCodec : IRpcCodec<List<Point>>
    {
        internal PointListCodec(IRpcCodecProvider provider)
        {
            ElementCodec = provider.GetCodec<Point>();
        }

        internal IRpcCodec<Point> ElementCodec { get; }

        public void Serialize(in List<Point> value, IBufferWriter<byte> buffer)
        {
        }

        public List<Point>? Deserialize(in ReadOnlySequence<byte> buffer) => [];
    }

    private sealed class NativeFactory<T>(Func<IRpcCodecProvider, IRpcCodec<T>> create)
        : ITestGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            if (adapterScope is not null)
                throw new ArgumentException("native test factories do not accept an adapter scope", nameof(adapterScope));
            return create(provider);
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class PointAdapter(RoutedPointCodec codec) : IRpcCodecAdapter
    {
        public string AdapterId => "nested-point-route/v1";
        public IRpcCodecAdapterScope CreateScope() => new PointScope(codec);
    }

    private sealed class PointScope(RoutedPointCodec codec) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
            => typeof(T) == typeof(Point)
                ? (IRpcCodec<T>)(object)codec
                : throw new NotSupportedException($"Unexpected nested route target '{typeof(T)}'.");

        public void Dispose()
        {
        }
    }

    private sealed class RoutedPointFactory(PointAdapter adapter) : ITestGeneratedCodecFactory
    {
        public Type TargetType => typeof(Point);
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<Point>() ??
               throw new ArgumentNullException(nameof(adapterScope));

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<Point>;
    }

    private sealed class ExplicitNoRouteCodec : IRpcCodec<NoRouteValue>
    {
        public void Serialize(in NoRouteValue value, IBufferWriter<byte> buffer) { }
        public NoRouteValue Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class GeneratedNoRouteCodec : IRpcCodec<NoRouteValue>
    {
        public void Serialize(in NoRouteValue value, IBufferWriter<byte> buffer) { }
        public NoRouteValue Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class NoRouteManifest : ITestGeneratedManifest
    {
        internal NoRouteManifest(Assembly ownerAssembly)
        {
            OwnerAssembly = ownerAssembly;
            Codecs =
            [
                new NativeFactory<NoRouteValue>(static _ => new GeneratedNoRouteCodec())
            ];
        }

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "no-route-test";
        public Assembly OwnerAssembly { get; }
        public string CompileTimeDescriptor => "no-route-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class PreviousApiManifest(Assembly ownerAssembly) : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api - 1;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "previous-api-test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "previous-api-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class NamedContractCodec(string name) : IRpcCodec<ContractValue>
    {
        internal string Name { get; } = name;
        public void Serialize(in ContractValue value, IBufferWriter<byte> buffer) { }
        public ContractValue Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class ContractCodecManifest(Assembly ownerAssembly, NamedContractCodec codec, string descriptor)
        : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "contract-codec-test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } =
            [new NativeFactory<ContractValue>(_ => codec)];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class CustomRuntimeContext : IRpcRuntimeContext
    {
        public IRpcCodecProvider Codecs { get; } = new ThrowingCodecProvider();
        public IRpcBufferWriterPool Buffers { get; } = new ThrowingBufferPool();
    }

    private sealed class ThrowingCodecProvider : IRpcCodecProvider
    {
        public IRpcCodec<T> GetCodec<T>() => throw new NotSupportedException();
    }

    private sealed class ThrowingBufferPool : IRpcBufferWriterPool
    {
        public IRpcByteBufferWriter Rent() => throw new NotSupportedException();
        public IRpcByteBufferWriter Rent(int maxWrittenBytes) => throw new NotSupportedException();
        public void Return(IRpcByteBufferWriter writer) { }
    }

    private sealed class NestedRouteManifest : ITestGeneratedManifest
    {
        internal NestedRouteManifest(Assembly ownerAssembly, RoutedPointCodec routedPoint)
        {
            OwnerAssembly = ownerAssembly;
            ContractCodecs =
            [
                new NativeFactory<Envelope>(static provider => new EnvelopeCodec(provider)),
                new NativeFactory<List<Point>>(static provider => new PointListCodec(provider)),
                new RoutedPointFactory(new PointAdapter(routedPoint))
            ];
        }

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "nested-route-test";
        public Assembly OwnerAssembly { get; }
        public string CompileTimeDescriptor => "nested-route-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }
}
