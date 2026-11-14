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
        var serviceProxy = harness.Client.CreateProxy<IOneWayInboundDrainService>();
        await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(10));
        var service = serviceProxy.Service;

        harness.Server.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var program = ((SharpLinkServer)harness.Server).CurrentAdmissionProgramForTests!;
        var held = await program.Controller.AcquireAsync(
            new SharpLinkAdmissionContext(1, 1, RpcMethodKind.OneWay, "held-control", null, null),
            1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "the actual server admission permit must be held before rejection");
        using (held.Lease)
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);

            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an immediately rejected OneWay request must not invoke the service method");
        }

        var probe = await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout);
        Ensure(probe == 52,
            "a unary request on the same connection should succeed after the rejected client stream drains to peer terminal");
        await harness.WaitForPhysicalDrainAsync();
    }

    [Test]
    [NotInParallel]
    public async Task UnknownBoundOneWayMethodShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var serviceProxy = harness.Client.CreateProxy<IOneWayInboundDrainService>();
        await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(10));
        var service = serviceProxy.Service;

        var channel = harness.Client.CreateContractChannel(
            harness.Client.GetLocalContract<IOneWayInboundDrainService>());
        await channel.PrepareAsync(PhaseTimeout);
        var method = UnknownOneWayMethod();
        var request = default(RpcEmptyRequest);
        var writer = new InputWriter(ManyPayloads(128, 32), channel.RuntimeContext.Codecs.GetCodec<byte[]>());
        await channel.InvokeOneWayAsync(method, in request, RpcEmptyRequestCodec.Instance,
            in writer, null).AsTask().WaitAsync(PhaseTimeout);
        Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
            "the actual unknown MethodId must perform zero business invocation");
        Ensure(await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout) == 52,
            "independent bounded input shape must permit safe drain without terminating the Session");
        await harness.WaitForPhysicalDrainAsync();
    }

    [Test]
    [NotInParallel]
    public async Task UnknownBoundOneWayMethodShouldDrainCompressedInputWithoutDecoding()
    {
        OneWayInboundDrainService.Reset();
        var compression = new CorruptingCompressionProvider();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
            options.Compression.Providers.Add(compression);
        });
        var serviceProxy = harness.Client.CreateProxy<IOneWayInboundDrainService>();
        await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(10));
        var service = serviceProxy.Service;

        var channel = harness.Client.CreateContractChannel(
            harness.Client.GetLocalContract<IOneWayInboundDrainService>());
        await channel.PrepareAsync(PhaseTimeout);
        var method = UnknownOneWayMethod();
        var requestPayload = Enumerable.Repeat((byte)0x67, 4096).ToArray();
        var codec = channel.RuntimeContext.Codecs.GetCodec<byte[]>();
        var writer = new InputWriter(ManyPayloads(128, 32), codec);
        await channel.InvokeOneWayAsync(method, in requestPayload, codec, in writer, null)
            .AsTask().WaitAsync(PhaseTimeout);
        Ensure(Volatile.Read(ref compression.CompressCount) > 0,
            "the large unknown-method head must use the actual negotiated compression provider");
        Ensure(Volatile.Read(ref compression.DecompressCount) == 0 && !OneWayInboundDrainService.Entered.IsCompleted,
            "unknown bound MethodId must drain without request/item decompression or business invocation");
        Ensure(await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout) == 52,
            "rejected corrupt compressed input must not poison the healthy Session");
        await harness.WaitForPhysicalDrainAsync();
    }

    private static RpcMethodDescriptor UnknownOneWayMethod()
    {
        var contract = GlobalCatalogManifestSource.Instance.CreateSnapshot().SelectMany(static item => item.Contracts)
            .Single(static item => item.ContractType == typeof(IOneWayInboundDrainService));
        Ensure(contract.Methods.All(static item => item.MethodId != long.MaxValue), "negative MethodId must really be absent");
        return contract.Methods.Single(static item => item.Name == nameof(IOneWayInboundDrainService.IgnoreStreamAfterGateAsync))
            .Facts with { MethodId = long.MaxValue };
    }

    private readonly struct InputWriter(IAsyncEnumerable<byte[]> items, IRpcCodec<byte[]> codec) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
            => new(sink.SendClientStreamAsync(requestId, 1, items, codec, cancellationToken));
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

        public bool TryCompress(
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
            return true;
        }

        public void Decompress(
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
                .UseTcp(0, IPAddress.Loopback)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(
                () => server.RunUntilStoppedAsync(cts.Token).AsTask(),
                CancellationToken.None);

            var client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure)
                .Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        internal async Task WaitForPhysicalDrainAsync()
        {
            var connection = ((ClientConnection[])typeof(SharpLinkClient).GetField(
                "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!).Single();
            var peer = ServerRegistryTestAccessor.ActiveConnections((SharpLinkServer)Server).Values.Single();
            var flow = ((RpcSessionProtocolState)typeof(RpcSession).GetField(
                "_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection.Session)!).FlowController!;
            using var timeout = new CancellationTokenSource(PhaseTimeout);
            while (connection.ActiveCallCount != 0 || connection.CallAdmissionReservationCount != 0 ||
                   peer.Session.StreamManager.ActiveStreamCount != 0 ||
                   flow.RetainedSendStreamCount != 0 || flow.SendConnectionCredit != 128)
                await Task.Delay(1, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

    }
}
