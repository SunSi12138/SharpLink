using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task DynamicContextDescriptorShouldPreserveLiveModuleLeaseKind(RpcMethodKind kind)
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .AddInterceptor(new CallContextTestInterceptor())
            .Build();
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new DescriptorReuseManifest();
        using var codecs = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, codecs);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "dynamic-descriptor-lease-kind", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        var hasRequestStreams = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        var isStream = hasRequestStreams || kind is RpcMethodKind.ServerStreaming;
        var stub = new GatedDescriptorReuseStub(new RpcMethodDescriptor(
            81, 17, kind, false, hasRequestStreams, false, null,
            ClientStreamCount: hasRequestStreams ? (ushort)1 : (ushort)0));
        var registration = ServiceRegistration.CreateSingleton(
            typeof(object), stub, new object(), ownsService: false, module);
        Task? invocation = null;
        try
        {
            var context = CreateInterceptedContext(server, connection, stub, requestId: 81);
            Ensure(session.StreamManager.ActiveStreamCount == (hasRequestStreams ? 1 : 0),
                "captured descriptor must reserve the real request stream route");
            invocation = InvokeWithDescriptorContext(server, registration, connection, context).AsTask();
            Ensure(!invocation.IsCompleted, "service gate must retain the invocation");
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == (isStream ? 1 : 0),
                "captured method kind must select the correct live module lease");
            Ensure(stub.LookupCount == 1, "module classification must reuse the exact context descriptor");
            Ensure(module.TryBeginDraining(), "module begins draining with the lease still owned");
            Ensure(!module.WaitForDrainAsync().IsCompleted, "drain must wait for the live invocation");
            stub.Complete();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            await module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
                "completion must release both module counters exactly once");
            Ensure(session.StreamManager.ActiveStreamCount == 0,
                "client-stream and duplex completion must release reserved request stream routes");
            module.AssertAccountingInvariant();
        }
        finally
        {
            stub.Complete();
            if (invocation is not null)
                await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            await registration.DisposeAsync();
            await connection.CloseAsync();
        }
    }

    private sealed class GatedDescriptorReuseStub(RpcMethodDescriptor descriptor) : IRpcStub
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int LookupCount { get; private set; }
        public long InterfaceHash => descriptor.ContractId;
        internal void Complete() => _completion.TrySetResult();
        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor method)
        {
            LookupCount++;
            method = descriptor;
            return methodHash == descriptor.MethodId;
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args) => new(_completion.Task);
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args,
            CancellationToken cancellationToken) => new(_completion.Task);
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args,
            IBufferWriter<byte> output) => new(_completion.Task);
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args,
            IBufferWriter<byte> output, CancellationToken cancellationToken) => new(_completion.Task);
    }
}
