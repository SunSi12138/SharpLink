namespace SharpLink.IntegrationTests;

public class OneWayEarlyRejectionDrainIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ImmediatelyRejectedOneWayClientStreamShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        TestService.ResetBlockingAdd();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        }, server => server.UseAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            options.QueueOneWayCalls = false;
        }));
        var service = await harness.Client.PrepareTestServiceAsync<IOneWayInboundDrainService>();
        var unary = await harness.Client.PrepareTestServiceAsync<ITestService>();
        var drain = new OneWayDrainObservation(harness.Client, harness.Server);
        var blocker = unary.BlockingAddAsync(1, 2, CancellationToken.None).AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(PhaseTimeout);
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);
            await drain.WaitForDrainAsync(PhaseTimeout);
            Ensure(!blocker.IsCompleted && !OneWayInboundDrainService.Entered.IsCompleted,
                "the occupied admission permit must reject OneWay without starting the service");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            OneWayInboundDrainService.Release();
        }

        Ensure(await blocker.WaitAsync(PhaseTimeout) == 3, "the original admission owner should complete");
        var probe = await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout);
        Ensure(probe == 52,
            "a unary request on the same connection should succeed after the rejected client stream drains to peer terminal");
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

        public static async Task<Harness> CreateAsync(
            Action<SharpLinkRuntimeOptions> runtimeConfigure, Action<SharpLinkServerBuilder>? serverConfigure = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure);
            serverConfigure?.Invoke(serverBuilder);
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
