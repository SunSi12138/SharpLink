using System.Buffers;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcReferencedCodecOwnerScopeRegressionTests
{
    [Test]
    public void ReferencedGeneratedCodecShouldResolveNestedCodecThroughProviderOwner()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);

        var providerManifest = new ProviderManifest(typeof(ReferencedPayload).Assembly);
        var providerRegistration = context.PrepareGeneratedManifest(providerManifest);
        context.PublishGeneratedCodecs(providerRegistration.Codecs, providerRegistration);
        context.AdoptGeneratedManifest(providerRegistration);

        var consumerManifest = new ConsumerManifest(typeof(IContractA).Assembly);
        var consumerRegistration = context.PrepareGeneratedManifest(consumerManifest);
        context.AdoptGeneratedManifest(consumerRegistration);

        var payloadCodec = RpcGeneratedCodecResolver
            .GetProvider(context, consumerManifest.OwnerAssembly)
            .GetCodec<ReferencedPayload>() as ReferencedPayloadCodec;

        Ensure(payloadCodec is not null,
            "the consumer must resolve the exact referenced provider registration");
        Ensure(payloadCodec!.Child is ReferencedChildCodec,
            "the referenced payload factory must resolve its nested child through the provider manifest's global graph, not the consumer Contract policy");
    }

    private sealed class ReferencedPayload { }
    private sealed class ReferencedChild { }

    private sealed class ReferencedChildCodec : IRpcCodec<ReferencedChild>
    {
        public void Serialize(in ReferencedChild value, IBufferWriter<byte> buffer) { }
        public ReferencedChild Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class ReferencedPayloadCodec(IRpcCodec<ReferencedChild> child) : IRpcCodec<ReferencedPayload>
    {
        internal IRpcCodec<ReferencedChild> Child { get; } = child;
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
                new ReferencedPayloadCodec(provider.GetCodec<ReferencedChild>()))
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
