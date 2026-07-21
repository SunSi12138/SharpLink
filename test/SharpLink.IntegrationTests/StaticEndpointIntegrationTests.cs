namespace SharpLink.IntegrationTests;

public sealed class StaticEndpointIntegrationTests
{
    [Test]
    public async Task StaticTcpEndpointsShouldConnectAndContinueWhenOneEndpointStops()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpoints(
                [
                    Endpoint("first", first.Port),
                    Endpoint("second", second.Port)
                ],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        Ensure(await service.PingAsync(41) == 42, "initial static-cluster RPC");

        await first.StopAsync();
        await Task.Delay(150);
        Ensure(await service.PingAsync(8) == 9, "remaining endpoint should serve RPC");
    }

    [Test]
    public async Task InvalidCustomSelectorShouldFailOnlyTheCurrentCall()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new InvalidSelector())
            .Build();

        await client.ConnectAsync();
        try
        {
            _ = await client.Get<IConnectionBehaviorService>().PingAsync(1);
            throw new Exception("invalid selector should fail the current call");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.FailedPrecondition)
        {
        }
    }

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
    };

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TcpServerScope : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _runTask;
        private int _stopped;

        private TcpServerScope(ISharpLinkServer server, int port)
        {
            _server = server;
            Port = port;
            _runTask = Task.Run(() => _server.RunAsync(_cancellation.Token).AsTask(), CancellationToken.None);
        }

        public int Port { get; }

        public static Task<TcpServerScope> StartAsync()
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new TcpServerScope(builder.Build(), port));
        }

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            await _server.StopAsync(TimeSpan.Zero);
            await _cancellation.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(1000));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _server.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    private sealed class InvalidSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => context.Count;
    }
}
