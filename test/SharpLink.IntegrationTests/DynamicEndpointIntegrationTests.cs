using System.Threading.Channels;

namespace SharpLink.IntegrationTests;

public sealed class DynamicEndpointIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task DynamicResolverShouldAddRemoveReplaceAndUpdateAttributesWithoutReconnecting()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("first", first.Port, "blue")]));
        var selector = new ZoneSelector("blue");
        var factoryCreates = 0;
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(
                resolver,
                endpoint =>
                {
                    Interlocked.Increment(ref factoryCreates);
                    return sockets(endpoint);
                })
            .UseEndpointSelector(selector)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            await client.ConnectAsync();
            var service = client.Get<IConnectionBehaviorService>();
            Ensure(await service.GetEndpointIdAsync() == "first", "initial resolver endpoint");

            resolver.Publish(new SharpLinkEndpointSnapshot(2,
            [
                Endpoint("first", first.Port, "blue"),
                Endpoint("second", second.Port, "green")
            ]));
            await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
            Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2, "added endpoint should become ready");
            Ensure(factoryCreates == 2, "only the added endpoint should create a factory");

            selector.Zone = "blue";
            Ensure(await service.GetEndpointIdAsync() == "first", "blue selector before attributes update");
            resolver.Publish(new SharpLinkEndpointSnapshot(3,
            [
                Endpoint("first", first.Port, "red"),
                Endpoint("second", second.Port, "blue")
            ]));
            await WaitUntilAsync(async () => await service.GetEndpointIdAsync() == "second", TimeSpan.FromSeconds(3));
            Ensure(factoryCreates == 2, "attributes-only update must not create a new endpoint factory");

            resolver.Publish(new SharpLinkEndpointSnapshot(4, [Endpoint("second", second.Port, "blue")]));
            await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(3));
            Ensure(await service.GetEndpointIdAsync() == "second", "removed endpoint must leave the candidate set");

            resolver.Publish(new SharpLinkEndpointSnapshot(5, [Endpoint("second", replacement.Port, "blue")]));
            await WaitUntilAsync(async () => await service.GetEndpointIdAsync() == "replacement", TimeSpan.FromSeconds(4));
            Ensure(factoryCreates == 3, "address change must create exactly one new generation factory");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(resolver.DisposeCount == 1, "dynamic client should dispose its resolver exactly once");
    }

    [Test]
    [NotInParallel]
    public async Task EmptyDynamicTopologyShouldRecoverWhenTheResolverPublishesAnEndpoint()
    {
        await using var server = await TcpServerScope.StartAsync("recovered");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, []));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .Build();

        await client.ConnectAsync();
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 0, "empty topology has no ready connection");
        resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("recovered", server.Port, "blue")]));
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(3));
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "recovered", "topology recovery RPC");
    }

    [Test]
    [NotInParallel]
    public async Task DynamicEndpointRemovalShouldDrainAnAcceptedStreamAndRouteNewCallsElsewhere()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green")
        ]));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new IdSelector("first"))
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        await using var stream = service.SlowRangeAsync(3, 80, CancellationToken.None).GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync() && stream.Current == 0, "first stream item");

        resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("second", second.Port, "green")]));
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(3));
        Ensure(await service.GetEndpointIdAsync() == "second", "new call after endpoint removal");
        Ensure(await stream.MoveNextAsync() && stream.Current == 1, "draining stream second item");
        Ensure(await stream.MoveNextAsync() && stream.Current == 2, "draining stream third item");
        Ensure(!await stream.MoveNextAsync(), "draining stream completion");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedDynamicFactoryReuseMustKeepTheLastGoodFactoryAlive()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("first", first.Port, "blue")]));
        var sockets = SharpLinkTransportFactories.Sockets();
        TrackingTransportFactory? factory = null;
        var factoryCreates = 0;
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(
                resolver,
                endpoint =>
                {
                    Interlocked.Increment(ref factoryCreates);
                    return factory ??= new TrackingTransportFactory(sockets(endpoint));
                })
            .Build();

        try
        {
            await client.ConnectAsync();
            var service = client.Get<IConnectionBehaviorService>();
            Ensure(await service.GetEndpointIdAsync() == "first", "initial dynamic endpoint");

            resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("replacement", replacement.Port, "green")]));
            await WaitUntilAsync(() => Volatile.Read(ref factoryCreates) == 2, TimeSpan.FromSeconds(3));

            Ensure(factory is not null && factory.DisposeCount == 0,
                "rejected snapshot must not dispose the last-good factory reference");
            Ensure(await service.GetEndpointIdAsync() == "first",
                "rejected snapshot must retain the last-good endpoint");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(factory is not null && factory.DisposeCount == 1,
            "last-good factory must be released exactly once during client stop");
    }

    [Test]
    [NotInParallel]
    public async Task ResolverWatchEndAndFailureShouldRetryAndRetainTheLastGoodTopology()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var recovered = await TcpServerScope.StartAsync("recovered");
        var resolver = new RestartingResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("first", first.Port, "blue")]),
            new SharpLinkEndpointSnapshot(2, [Endpoint("recovered", recovered.Port, "green")]));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "first", "initial last-good topology");
        await WaitUntilAsync(async () => await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "recovered",
            TimeSpan.FromSeconds(4));
        Ensure(resolver.ResolveCount >= 3, "watch end should retry Resolve after one resolver failure");
    }

    [Test]
    [NotInParallel]
    public async Task DnsEndpointHelperShouldResolveLocalhostAndPreserveHostnameAuthority()
    {
        await using var server = await TcpServerScope.StartAsync("dns");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseDnsEndpoints(
                "localhost",
                server.Port,
                SharpLinkTransportFactories.Sockets(),
                options =>
                {
                    options.AddressFamily = AddressFamily.InterNetwork;
                    options.RefreshInterval = TimeSpan.FromMilliseconds(10);
                    options.MinimumRefreshInterval = TimeSpan.FromMilliseconds(1);
                })
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "dns", "DNS discovery RPC");
    }

    private static SharpLinkEndpoint Endpoint(string id, int port, string zone) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port),
        Attributes = new Dictionary<string, string> { ["zone"] = zone }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(20);
        if (!condition())
            throw new TimeoutException("Dynamic topology did not reach the expected state.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                if (await condition())
                    return;
            }
            catch (SharpLinkException)
            {
                // A generation replacement deliberately has a short interval with no ready endpoint.
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Dynamic topology did not reach the expected state.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots = Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        public void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
                _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingTransportFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => inner.ConnectAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ZoneSelector(string zone) : ISharpLinkEndpointSelector
    {
        private string _zone = zone;

        public string Zone
        {
            get => Volatile.Read(ref _zone);
            set => Volatile.Write(ref _zone, value);
        }

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            var zone = Zone;
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) == 0 &&
                    context[index].Endpoint.Attributes.TryGetValue("zone", out var candidateZone) &&
                    candidateZone == zone)
                {
                    return index;
                }
            }
            return -1;
        }
    }

    private sealed class IdSelector(string id) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) == 0 && context[index].Endpoint.Id == id)
                    return index;
            }
            return -1;
        }
    }

    private sealed class RestartingResolver(
        SharpLinkEndpointSnapshot initial,
        SharpLinkEndpointSnapshot recovered) : ISharpLinkEndpointResolver
    {
        private int _resolveCount;

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _resolveCount);
            return count == 2
                ? ValueTask.FromException<SharpLinkEndpointSnapshot>(new InvalidOperationException("transient resolver failure"))
                : ValueTask.FromResult(count >= 3 ? recovered : initial);
        }

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        public static Task<TcpServerScope> StartAsync(string endpointId)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            builder.ReplaceService<IConnectionBehaviorService>(new ConnectionBehaviorService { EndpointId = endpointId });
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new TcpServerScope(builder.Build(), port));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                await _server.StopAsync(TimeSpan.Zero);
                await _cancellation.CancelAsync();
                await Task.WhenAny(_runTask, Task.Delay(1000));
            }
            await _server.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
