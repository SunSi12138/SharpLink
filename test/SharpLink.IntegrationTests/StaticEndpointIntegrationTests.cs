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

    [Test]
    public async Task ConcurrentConnectAndStopShouldConvergeStaticClusterResources()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .Build();
        try
        {
            var connects = new Task[16];
            for (var index = 0; index < connects.Length; index++)
                connects[index] = client.ConnectAsync().AsTask();
            await Task.WhenAll(connects);

            var stops = new Task[16];
            for (var index = 0; index < stops.Length; index++)
                stops[index] = client.StopAsync().AsTask();
            await Task.WhenAll(stops);

            var implementation = (SharpLinkClient)client;
            Ensure(implementation.State == SharpLinkConnectionState.Stopped, "static cluster must stop");
            Ensure(implementation.ReadyConnectionCount == 0, "static cluster connections must converge to zero");
            await EnsureThrows<SharpLinkException>(client.ConnectAsync().AsTask(), "Connect after Stop");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task RoundRobinAndCustomAttributeSelectorsShouldChooseExpectedEndpoints()
    {
        await using var first = await TcpServerScope.StartAsync("east");
        await using var second = await TcpServerScope.StartAsync("west");
        var endpoints = new[]
        {
            Endpoint("first", first.Port, "east"),
            Endpoint("second", second.Port, "west")
        };

        await using (var roundRobin = SharpClientBuilder.Create()
                         .UseSerializer(MemoryPackCodec.Resolver)
                         .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
                         .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
                         .Build())
        {
            await roundRobin.ConnectAsync();
            var service = roundRobin.Get<IConnectionBehaviorService>();
            var ids = new[]
            {
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync()
            };
            Ensure(ids[0] != ids[1] && ids[0] == ids[2] && ids[1] == ids[3], "round robin endpoint order");
        }

        await using var custom = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new AttributeSelector("west"))
            .Build();
        await custom.ConnectAsync();
        Ensure(await custom.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "west", "custom selector attributes");
    }

    [Test]
    public async Task LeastPendingShouldAvoidEndpointWithAnActiveCall()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending)
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        var slow = service.SlowAsync(200, CancellationToken.None).AsTask();
        var completed = await Task.WhenAny(first.Service.SlowCallStarted!.Task, second.Service.SlowCallStarted!.Task);
        var busyId = await ((Task<string>)completed);
        var selectedId = await service.GetEndpointIdAsync();
        Ensure(selectedId != busyId, "least pending should select the non-busy endpoint");
        await slow;
    }

    private static SharpLinkEndpoint Endpoint(string id, int port, string? zone = null) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port),
        Attributes = zone is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["zone"] = zone }
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

    private static async Task EnsureThrows<TException>(Task action, string name) where TException : Exception
    {
        try
        {
            await action;
            throw new Exception($"{name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private sealed class TcpServerScope : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _runTask;
        private int _stopped;

        private TcpServerScope(ISharpLinkServer server, int port, ConnectionBehaviorService service)
        {
            _server = server;
            Port = port;
            Service = service;
            _runTask = Task.Run(() => _server.RunAsync(_cancellation.Token).AsTask(), CancellationToken.None);
        }

        public int Port { get; }
        public ConnectionBehaviorService Service { get; }

        public static Task<TcpServerScope> StartAsync(string endpointId = "default")
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            var service = new ConnectionBehaviorService
            {
                EndpointId = endpointId,
                SlowCallStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            builder.ReplaceService<IConnectionBehaviorService>(service);
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new TcpServerScope(builder.Build(), port, service));
        }

        public static Task<TcpServerScope> StartNamedPipeAsync(string name)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseNamedPipe(name)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0, new ConnectionBehaviorService()));
        }

        public static Task<TcpServerScope> StartSharedMemoryAsync(string name)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseSharedMemory(name)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0, new ConnectionBehaviorService()));
        }

        public static Task<TcpServerScope> StartUdsAsync(string path)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseUds(path)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new TcpServerScope(builder.Build(), 0, new ConnectionBehaviorService()));
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

    private sealed class AttributeSelector(string zone) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) == 0 &&
                    context[index].Endpoint.Attributes.TryGetValue("zone", out var value) && value == zone)
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
