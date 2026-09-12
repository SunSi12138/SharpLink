using System.Buffers;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcReferencedCodecOwnerScopeRegressionTests
{
    [Test]
    public void ReferencedGeneratedCodecShouldResolveNestedCodecThroughFrozenProviderOwner()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(new EndpointReferencedChildCodec())
            .UseCodecResolver(static type => type == typeof(ReferencedFallbackChild)
                ? new EndpointFallbackChildCodec()
                : null)
            .Build(includeGeneratedAssemblyCatalog: false);

        var providerManifest = new ProviderManifest(typeof(ReferencedPayload).Assembly);
        var providerRegistration = context.PrepareGeneratedManifest(providerManifest);
        context.PublishGeneratedCodecs(providerRegistration.Codecs, providerRegistration);
        context.AdoptGeneratedManifest(providerRegistration);

        var localPayloadCodec = RpcGeneratedCodecResolver
            .GetProvider(context, providerManifest.OwnerAssembly)
            .GetCodec<ReferencedPayload>() as ReferencedPayloadCodec;
        AssertFrozenOwnerGraph(localPayloadCodec, "provider manifest local resolution");

        var consumerManifest = new ConsumerManifest(typeof(IContractA).Assembly);
        var consumerRegistration = context.PrepareGeneratedManifest(consumerManifest);
        context.AdoptGeneratedManifest(consumerRegistration);

        var referencedPayloadCodec = RpcGeneratedCodecResolver
            .GetProvider(context, consumerManifest.OwnerAssembly)
            .GetCodec<ReferencedPayload>() as ReferencedPayloadCodec;
        AssertFrozenOwnerGraph(referencedPayloadCodec, "referenced consumer resolution");
    }

    private static void AssertFrozenOwnerGraph(ReferencedPayloadCodec? payloadCodec, string path)
    {
        Ensure(payloadCodec is not null,
            $"{path} must resolve the provider-owned generated payload registration");
        Ensure(payloadCodec!.Child is ReferencedChildCodec,
            $"{path} must resolve the generated child through the provider manifest's frozen global graph, not an endpoint AddCodec override");
        Ensure(payloadCodec.FallbackChild is not EndpointFallbackChildCodec,
            $"{path} must resolve unmanaged fallback semantics from the provider manifest's frozen graph, not endpoint UseCodecResolver state");
        Ensure(payloadCodec.FallbackChild.GetType().Name.Contains("UnsafeBlitCodec", StringComparison.Ordinal),
            $"{path} must use the compile-time unmanaged fallback strategy");
    }

    private sealed class ReferencedPayload { }
    private sealed class ReferencedChild { }
    private readonly record struct ReferencedFallbackChild(int Value);

    private sealed class ReferencedChildCodec : IRpcCodec<ReferencedChild>
    {
        public void Serialize(in ReferencedChild value, IBufferWriter<byte> buffer) { }
        public ReferencedChild Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class EndpointReferencedChildCodec : IRpcCodec<ReferencedChild>
    {
        public void Serialize(in ReferencedChild value, IBufferWriter<byte> buffer) { }
        public ReferencedChild Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class EndpointFallbackChildCodec : IRpcCodec<ReferencedFallbackChild>
    {
        public void Serialize(in ReferencedFallbackChild value, IBufferWriter<byte> buffer) { }
        public ReferencedFallbackChild Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class ReferencedPayloadCodec(
        IRpcCodec<ReferencedChild> child,
        IRpcCodec<ReferencedFallbackChild> fallbackChild) : IRpcCodec<ReferencedPayload>
    {
        internal IRpcCodec<ReferencedChild> Child { get; } = child;
        internal IRpcCodec<ReferencedFallbackChild> FallbackChild { get; } = fallbackChild;
        public void Serialize(in ReferencedPayload value, IBufferWriter<byte> buffer) { }
        public ReferencedPayload Deserialize(in ReadOnlySequence<byte> buffer) => new();
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
                throw new ArgumentException("native regression factory does not accept an Adapter scope", nameof(adapterScope));
            return create(provider);
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class ProviderManifest(Assembly ownerAssembly) : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "referenced-provider-owner-scope-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "referenced-provider-owner-scope-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
        [
            new NativeFactory<ReferencedChild>(static _ => new ReferencedChildCodec()),
            new NativeFactory<ReferencedPayload>(static provider =>
                new ReferencedPayloadCodec(
                    provider.GetCodec<ReferencedChild>(),
                    provider.GetCodec<ReferencedFallbackChild>()))
        ];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ConsumerManifest(Assembly ownerAssembly)
        : ITestGeneratedManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "referenced-consumer-owner-scope-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "referenced-consumer-owner-scope-regression";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; } =
        [
            new(typeof(ReferencedPayload), TestGeneratedIdentity.CodecHash)
        ];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
