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

    [Test]
    public async Task StaticClusterShouldExpandWithinGlobalAndPerEndpointBudgets()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 2;
            })
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        var calls = new Task<int>[32];
        for (var index = 0; index < calls.Length; index++)
            calls[index] = service.SlowAsync(100, CancellationToken.None).AsTask();
        await Task.WhenAll(calls);

        var implementation = (SharpLinkClient)client;
        await WaitUntilAsync(() => implementation.ReadyConnectionCount == 4, TimeSpan.FromSeconds(2));
        Ensure(implementation.ReadyConnectionCount == 4, "cluster should fill only the configured global budget");
    }

    [Test]
    public async Task StaticNamedPipeEndpointsShouldServeRpc()
    {
        var firstName = $"sharplink-static-first-{Guid.NewGuid():N}";
        var secondName = $"sharplink-static-second-{Guid.NewGuid():N}";
        await using var first = await TcpServerScope.StartNamedPipeAsync(firstName);
        await using var second = await TcpServerScope.StartNamedPipeAsync(secondName);
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [
                    new SharpLinkEndpoint { Id = "first", Address = new SharpLinkNamedPipeAddress(firstName) },
                    new SharpLinkEndpoint { Id = "second", Address = new SharpLinkNamedPipeAddress(secondName) }
                ],
                SharpLinkTransportFactories.NamedPipes())
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().PingAsync(4) == 5, "named-pipe static-cluster RPC");
    }

    [Test]
    public async Task StaticSharedMemoryEndpointsShouldServeRpc()
    {
        var firstName = $"sharplink-static-first-{Guid.NewGuid():N}";
        var secondName = $"sharplink-static-second-{Guid.NewGuid():N}";
        await using var first = await TcpServerScope.StartSharedMemoryAsync(firstName);
        await using var second = await TcpServerScope.StartSharedMemoryAsync(secondName);
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [
                    new SharpLinkEndpoint { Id = "first", Address = new SharpLinkSharedMemoryAddress(firstName) },
                    new SharpLinkEndpoint { Id = "second", Address = new SharpLinkSharedMemoryAddress(secondName) }
                ],
                SharpLinkTransportFactories.SharedMemory())
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().PingAsync(6) == 7, "shared-memory static-cluster RPC");
    }

    [Test]
    public async Task StaticUdsEndpointsShouldServeRpc()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;
        var firstPath = Path.Combine(Path.GetTempPath(), $"sharplink-static-{Guid.NewGuid():N}.sock");
        var secondPath = Path.Combine(Path.GetTempPath(), $"sharplink-static-{Guid.NewGuid():N}.sock");
        await using var first = await TcpServerScope.StartUdsAsync(firstPath);
        await using var second = await TcpServerScope.StartUdsAsync(secondPath);
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [
                    new SharpLinkEndpoint { Id = "first", Address = new SharpLinkUnixDomainSocketAddress(firstPath) },
                    new SharpLinkEndpoint { Id = "second", Address = new SharpLinkUnixDomainSocketAddress(secondPath) }
                ],
                SharpLinkTransportFactories.Sockets())
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().PingAsync(10) == 11, "UDS static-cluster RPC");
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(20);
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

        public static Task<TcpServerScope> StartNamedPipeAsync(string name)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseNamedPipe(name)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0));
        }

        public static Task<TcpServerScope> StartSharedMemoryAsync(string name)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseSharedMemory(name)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0));
        }

        public static Task<TcpServerScope> StartUdsAsync(string path)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseUds(path)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0));
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
