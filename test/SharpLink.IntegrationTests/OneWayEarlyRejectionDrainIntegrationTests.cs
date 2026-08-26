using System.Collections.Frozen;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class OneWayEarlyRejectionDrainIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ImmediatelyRejectedOneWayClientStreamShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var service = harness.Client.Get<IOneWayInboundDrainService>();

        using (harness.RejectOneWayInboundDrainServiceCalls())
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);

            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an immediately rejected OneWay request must not invoke the service method");
        }

        var probe = await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout);
        Ensure(probe == 52,
            "a unary request on the same connection should succeed after the rejected client stream drains to peer terminal");
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodShapeShouldTerminateInsteadOfStallingOversizedStream()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var service = harness.Client.Get<IOneWayInboundDrainService>();

        using (harness.HideOneWayInboundDrainMethodShape())
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            var failure = await CaptureFailureAsync(send);

            Ensure(failure is not null,
                "an unresolved OneWay method shape should terminate the connection instead of completing successfully");
            Ensure(failure is not TimeoutException,
                "an unresolved OneWay method shape must not leave oversized client-stream data stalled behind receive flow control");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an unresolved OneWay method must not invoke the service method");
        }
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodShapeShouldTerminateBeforeCompressedDecodeAndStreamWindowStalls()
    {
        OneWayInboundDrainService.Reset();
        var compression = new CorruptingCompressionProvider();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
            options.Compression.Providers.Add(compression);
        });
        var service = harness.Client.Get<IOneWayInboundDrainService>();

        using (harness.HideOneWayInboundDrainMethodShape())
        {
            var requestPayload = Enumerable.Repeat((byte)0x67, 4096).ToArray();
            var send = service.IgnoreCorruptiblePayloadAndStreamAsync(
                requestPayload,
                ManyPayloads(128, 32)).AsTask();
            var failure = await CaptureFailureAsync(send);

            Ensure(Volatile.Read(ref compression.CompressCount) > 0,
                "the large OneWay request payload must use the negotiated compression provider");
            Ensure(Volatile.Read(ref compression.DecompressCount) == 0,
                "an unresolved OneWay method shape must terminate before invoking the request decompressor");
            Ensure(failure is not null,
                "an unresolved compressed OneWay shape should terminate the connection");
            Ensure(failure is not TimeoutException,
                "an unresolved compressed OneWay shape must not leave subsequent StreamData stalled behind receive flow control");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an unresolved compressed OneWay request must not invoke the service method");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(PhaseTimeout);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async IAsyncEnumerable<byte[]> ManyPayloads(
        int count,
        int size,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Enumerable.Repeat((byte)0x4d, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return payload;
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CorruptingCompressionProvider : ISharpLinkCompressionProvider
    {
        internal int CompressCount;
        internal int DecompressCount;

        public string WireProfile => "corrupt-oneway-request";

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxOutputBytes < 1)
                throw new InvalidOperationException("test compression output budget is empty");

            Interlocked.Increment(ref CompressCount);
            var span = output.GetSpan(1);
            span[0] = 0x7f;
            output.Advance(1);
            return new SharpLinkCompressionResult(checked((int)input.Length), 1);
        }

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _ = input;
            _ = output;
            _ = maxOutputBytes;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DecompressCount);
            throw new InvalidDataException("intentional corrupt compressed request body");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private static readonly FieldInfo ServicesField = typeof(SharpLinkServer).GetField(
            "_services", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cannot find server service registry");
        private static readonly FieldInfo DynamicModuleStateField = typeof(SharpLinkDynamicModule).GetField(
            "_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cannot find dynamic-module state field");

        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
        }

        public static async Task<Harness> CreateAsync(Action<SharpLinkRuntimeOptions> runtimeConfigure)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(
                () => server.RunAsync(cts.Token).AsTask(),
                CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure)
                .Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        public IDisposable RejectOneWayInboundDrainServiceCalls()
        {
            var server = (SharpLinkServer)Server;
            var current = (FrozenDictionary<long, ServiceRegistration>)(
                ServicesField.GetValue(server)
                ?? throw new InvalidOperationException("server service registry is unavailable"));
            var target = current.Single(static pair =>
                pair.Value.ContractType == typeof(IOneWayInboundDrainService));

            var module = (SharpLinkDynamicModule)RuntimeHelpers.GetUninitializedObject(
                typeof(SharpLinkDynamicModule));
            DynamicModuleStateField.SetValue(
                module,
                (int)SharpLinkDynamicModuleState.Draining);
            var replacement = ServiceRegistration.CreateSingleton(
                target.Value.ContractType,
                target.Value.Stub,
                new OneWayInboundDrainService(),
                ownsService: false,
                module: module);
            var updated = current.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            updated[target.Key] = replacement;
            ServicesField.SetValue(server, updated.ToFrozenDictionary());
            Thread.MemoryBarrier();
            return new RestoreServicesScope(server, current);
        }

        public IDisposable HideOneWayInboundDrainMethodShape()
        {
            var server = (SharpLinkServer)Server;
            var current = (FrozenDictionary<long, ServiceRegistration>)(
                ServicesField.GetValue(server)
                ?? throw new InvalidOperationException("server service registry is unavailable"));
            var target = current.Single(static pair =>
                pair.Value.ContractType == typeof(IOneWayInboundDrainService));

            var replacement = ServiceRegistration.CreateSingleton(
                target.Value.ContractType,
                new UnknownMethodShapeStub(target.Value.Stub.InterfaceHash),
                new OneWayInboundDrainService(),
                ownsService: false);
            var updated = current.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            updated[target.Key] = replacement;
            ServicesField.SetValue(server, updated.ToFrozenDictionary());
            Thread.MemoryBarrier();
            return new RestoreServicesScope(server, current);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private sealed class UnknownMethodShapeStub(long interfaceHash) : IRpcStub
        {
            public long InterfaceHash { get; } = interfaceHash;

            public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
            {
                _ = methodHash;
                descriptor = default;
                return false;
            }

            public bool SupportsCancellation(long methodHash)
            {
                _ = methodHash;
                return false;
            }

            public ValueTask InvokeNoReturnAsync(
                object service,
                IRpcGeneratedServerBridge bridge,
                long methodHash,
                long requestId,
                ReadOnlySequence<byte> args)
                => throw UnexpectedInvocation();

            public ValueTask InvokeNoReturnCancellableAsync(
                object service,
                IRpcGeneratedServerBridge bridge,
                long methodHash,
                long requestId,
                ReadOnlySequence<byte> args,
                CancellationToken cancellationToken)
                => throw UnexpectedInvocation();

            public ValueTask InvokeAsync(
                object service,
                IRpcGeneratedServerBridge bridge,
                long methodHash,
                long requestId,
                ReadOnlySequence<byte> args,
                IBufferWriter<byte> output)
                => throw UnexpectedInvocation();

            public ValueTask InvokeCancellableAsync(
                object service,
                IRpcGeneratedServerBridge bridge,
                long methodHash,
                long requestId,
                ReadOnlySequence<byte> args,
                IBufferWriter<byte> output,
                CancellationToken cancellationToken)
                => throw UnexpectedInvocation();

            private static InvalidOperationException UnexpectedInvocation()
                => new("unresolved method-shape stub must never be invoked");
        }

        private sealed class RestoreServicesScope(
            SharpLinkServer server,
            FrozenDictionary<long, ServiceRegistration> original) : IDisposable
        {
            private SharpLinkServer? _server = server;

            public void Dispose()
            {
                var currentServer = Interlocked.Exchange(ref _server, null);
                if (currentServer is null)
                    return;
                ServicesField.SetValue(currentServer, original);
                Thread.MemoryBarrier();
            }
        }
    }
}
