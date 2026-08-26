using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.StaticCodecOwnerTest.Contracts;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class StaticContractCodecProviderRegressionTests
{
    [Test]
    public async Task ServerBuildShouldBindAutomaticAndReplacementStubsByAssemblyProvider()
    {
        var manifest = new TwoContractManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            var server = SharpLinkServerBuilder.Create()
                .UseTransport(new NoopListener())
                .DisableAutomaticServiceRegistration()
                .EnableService<IContractA>()
                .ReplaceService<IContractB>(new ContractBService())
                .Build();
            try
            {
                Ensure(ReferenceEquals(manifest.CapturedA, manifest.SharedCodec),
                    "automatic service A must bind the assembly-owned Codec provider");
                Ensure(ReferenceEquals(manifest.CapturedB, manifest.SharedCodec),
                    "replacement service B must bind the assembly-owned Codec provider");
                Ensure(ReferenceEquals(manifest.CapturedA, manifest.CapturedB),
                    "same-assembly Contracts must share one assembly-owned Codec graph");
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TwoContractManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private const string FingerprintA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string FingerprintB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly IReadOnlyList<SharpLinkGeneratedContractDescriptor> _contracts;
        private readonly IReadOnlyList<SharpLinkGeneratedServiceDescriptor> _services;
        private readonly IReadOnlyList<IRpcGeneratedCodecFactory> _contractCodecs;

        internal TwoContractManifest()
        {
            SharedCodec = new AssemblySharedCodec();
            _contracts =
            [
                new SharpLinkGeneratedContractDescriptor(
                    typeof(IContractA),
                    "ContractA",
                    910001,
                    FingerprintA,
                    [],
                    static (_, _) => throw new NotSupportedException(),
                    provider =>
                    {
                        CapturedA = provider.GetCodec<SharedPayload>();
                        return new StubMarker(910001);
                    }),
                new SharpLinkGeneratedContractDescriptor(
                    typeof(IContractB),
                    "ContractB",
                    910002,
                    FingerprintB,
                    [],
                    static (_, _) => throw new NotSupportedException(),
                    provider =>
                    {
                        CapturedB = provider.GetCodec<SharedPayload>();
                        return new StubMarker(910002);
                    })
            ];
            _services =
            [
                new SharpLinkGeneratedServiceDescriptor(
                    typeof(IContractA),
                    typeof(ContractAService),
                    "ContractA",
                    typeof(ContractAService).FullName!,
                    910001,
                    FingerprintA,
                    SharpLinkServiceLifetime.Singleton,
                    [],
                    static _ => new ContractAService()),
                new SharpLinkGeneratedServiceDescriptor(
                    typeof(IContractB),
                    typeof(ContractBService),
                    "ContractB",
                    typeof(ContractBService).FullName!,
                    910002,
                    FingerprintB,
                    SharpLinkServiceLifetime.Singleton,
                    [],
                    static _ => new ContractBService())
            ];
            _contractCodecs = [new DirectFactory(SharedCodec, "test/assembly-shared")];
        }

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(IContractA).Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => _contracts;
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => _services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => _contractCodecs;
        public IReadOnlyList<string> Dependencies => [];

        internal AssemblySharedCodec SharedCodec { get; }
        internal IRpcCodec<SharedPayload>? CapturedA { get; private set; }
        internal IRpcCodec<SharedPayload>? CapturedB { get; private set; }

        private sealed class DirectFactory(IRpcCodec<SharedPayload> codec, string schemaId)
            : IRpcGeneratedCodecFactory
        {
            public Type TargetType => typeof(SharedPayload);
            public string SchemaId => schemaId;
            public string WireFormatId => "test/shared-payload/v1";
            public RpcGeneratedCodecFactoryKind Kind => RpcGeneratedCodecFactoryKind.Direct;
            public string? AdapterId => null;
            public IRpcCodecAdapter? Adapter => null;

            public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
                => codec;

            public bool IsCompatibleCodec(IRpcCodec candidate)
                => candidate is IRpcCodec<SharedPayload>;
        }
    }

    private sealed class AssemblySharedCodec : IRpcCodec<SharedPayload>
    {
        public void Serialize(in SharedPayload value, IBufferWriter<byte> buffer)
        {
        }

        public SharedPayload Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class StubMarker(long interfaceHash) : IRpcStub
    {
        public long InterfaceHash => interfaceHash;

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcSession session,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcSession session,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(
            object service,
            IRpcSession session,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IRpcByteBufferWriter output) => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcSession session,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IRpcByteBufferWriter output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
