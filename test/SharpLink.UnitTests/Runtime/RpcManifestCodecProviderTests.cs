using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class RpcManifestCodecProviderTests
{
    [Test]
    public void ManifestScopedRouteShouldFlowIntoNativeDtoAndCollectionDependencies()
    {
        var routedPoint = new RoutedPointCodec();
        var ownerAssembly = typeof(RpcManifestCodecProviderTests).Assembly;
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
    public void NoRouteTemporaryBindingShouldMatchStaticContextCodecPrecedence()
    {
        var explicitCodec = new ExplicitNoRouteCodec();
        var ownerAssembly = typeof(RpcManifestCodecProviderTests).Assembly;
        using var context = new SharpLinkRuntimeContextBuilder()
  .AddCodec(explicitCodec)
  .Build(includeGeneratedAssemblyCatalog: false);
        using var registration = context.PrepareGeneratedManifest(new NoRouteManifest(ownerAssembly));

        var staticProvider = RpcGeneratedCodecResolver.GetProvider(context, ownerAssembly);
        var dynamicCandidateProvider = RpcGeneratedCodecResolver.GetProvider(registration);

        Ensure(ReferenceEquals(staticProvider.GetCodec<NoRouteValue>(), explicitCodec),
  "static no-route Contract binding must honor the explicit context Codec");
        Ensure(ReferenceEquals(dynamicCandidateProvider.GetCodec<NoRouteValue>(), explicitCodec),
  "dynamic candidate binding must use the same no-route precedence as static binding");
    }

    [Test]
    public void RuntimeContextBuildShouldRejectPreviousGeneratedManifestApi()
    {
        try
        {
  using var _ = new SharpLinkRuntimeContextBuilder().Build(
      [new PreviousApiManifest(typeof(RpcManifestCodecProviderTests).Assembly)]);
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
                throw new ArgumentException("native test factories do not accept an adapter scope", nameof(adapterScope));
            return create(provider);
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class PointAdapter(RoutedPointCodec codec) : IRpcCodecAdapter
    {
        public string AdapterId => "nested-point-route/v1";
        public string WireFormatId => "nested-point-wire/v1";
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

    private sealed class RoutedPointFactory(PointAdapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(Point);
        public string SchemaId => "nested-point-route";
        public string WireFormatId => adapter.WireFormatId;
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

    private sealed class NoRouteManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal NoRouteManifest(Assembly ownerAssembly)
        {
  OwnerAssembly = ownerAssembly;
  Codecs =
  [
      new NativeFactory<NoRouteValue>(static _ => new GeneratedNoRouteCodec(), "no-route-generated")
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

    private sealed class PreviousApiManifest(Assembly ownerAssembly) : ISharpLinkGeneratedAssemblyManifest
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

    private sealed class NestedRouteManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal NestedRouteManifest(Assembly ownerAssembly, RoutedPointCodec routedPoint)
        {
            OwnerAssembly = ownerAssembly;
            Codecs =
            [
                new NativeFactory<Envelope>(static provider => new EnvelopeCodec(provider), "nested-envelope-native"),
                new NativeFactory<List<Point>>(static provider => new PointListCodec(provider), "nested-list-native"),
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
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<Type> ManifestScopedCodecTargets => [typeof(Point)];
        public IReadOnlyList<string> Dependencies => [];
    }
}
