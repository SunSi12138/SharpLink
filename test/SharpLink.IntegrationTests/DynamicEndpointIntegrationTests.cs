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
    public async Task CustomDynamicSelectorShouldRejectTheOnlyNonMatchingReadyEndpoint()
    {
        await using var east = await TcpServerScope.StartAsync("east");
        await using var west = await TcpServerScope.StartAsync("west");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("east", east.Port, "east"),
            Endpoint("west", west.Port, "west")
        ]));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new ZoneSelector("west"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
        resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("east", east.Port, "east")]));
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(3));

        var exception = await CaptureSharpLinkException(
            client.Get<IConnectionBehaviorService>().GetEndpointIdAsync().AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition,
            "a strict dynamic selector must not be bypassed for one candidate");
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
    public async Task DynamicInitialConnectShouldFailWhenEveryResolvedEndpointFails()
    {
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("failed", 1, "red")]));
        var factory = new FailingConnectFactory();
        var client = SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        try
        {
            var first = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
            var second = await CaptureSharpLinkException(client.ConnectAsync().AsTask());

            Ensure(first.Code == SharpLinkErrorCode.Unavailable, "initial failed dynamic topology error");
            Ensure(second.Code == SharpLinkErrorCode.Unavailable, "failed initial dynamic topology must not cache success");
            Ensure(factory.ConnectCount != 0, "resolved endpoint connection must have been attempted");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task FailedInitialDynamicDialShouldReconnectWithoutANewerResolverVersion()
    {
        await using var server = await TcpServerScope.StartAsync("recovered");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("recovered", server.Port, "green")]));
        var factory = new FailOnceConnectFactory(SharpLinkTransportFactories.Sockets()(Endpoint("recovered", server.Port, "green")));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        var initial = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
        Ensure(initial.Code == SharpLinkErrorCode.Unavailable, "initial failed dial reports unavailable");

        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(3));
        Ensure(factory.ConnectCount >= 2, "same accepted topology must reconnect after the first dial fails");
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "recovered",
            "same-version dynamic topology recovers after its reconnect worker succeeds");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedDynamicSnapshotCleanupShouldContinueAfterFactoryDisposalFailure()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("first", first.Port, "blue")]));
        var sockets = SharpLinkTransportFactories.Sockets();
        TrackingTransportFactory? goodFactory = null;
        var throwingFactory = new ThrowingDisposeFactory();
        var remainingFactory = new FailingConnectFactory();
        var factoryCreates = 0;
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(
                resolver,
                endpoint =>
                {
                    Interlocked.Increment(ref factoryCreates);
                    return endpoint.Id switch
                    {
                        "first" => goodFactory ??= new TrackingTransportFactory(sockets(endpoint)),
                        "throwing" => throwingFactory,
                        "remaining" => remainingFactory,
                        _ => throw new InvalidOperationException("test factory construction failure")
                    };
                })
            .Build();

        try
        {
            await client.ConnectAsync();
            var service = client.Get<IConnectionBehaviorService>();
            Ensure(await service.GetEndpointIdAsync() == "first", "initial retained topology");

            resolver.Publish(new SharpLinkEndpointSnapshot(2,
            [
                Endpoint("throwing", first.Port, "red"),
                Endpoint("remaining", first.Port, "green"),
                Endpoint("factory-failure", first.Port, "yellow")
            ]));
            await WaitUntilAsync(
                () => Volatile.Read(ref factoryCreates) == 4 &&
                      throwingFactory.DisposeCount == 1 &&
                      remainingFactory.DisposeCount == 1,
                TimeSpan.FromSeconds(3));

            Ensure(goodFactory is not null && goodFactory.DisposeCount == 0,
                "failed cleanup must not affect the last-good factory");
            Ensure(await service.GetEndpointIdAsync() == "first",
                "failed snapshot cleanup must retain the last-good topology");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(goodFactory is not null && goodFactory.DisposeCount == 1,
            "last-good factory disposal after rejected snapshot cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task DynamicReplacementShouldWaitForExcessRetiringConnectionsToDrain()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("first", first.Port, "blue")]));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = 0;
            })
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        await using var stream = service.SlowRangeAsync(3, 100, CancellationToken.None).GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync() && stream.Current == 0, "initial active stream");

        resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("second", second.Port, "green")]));
        await Task.Delay(150);
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 0,
            "retiring budget must defer replacement connection creation while an old stream is active");
        Ensure(await stream.MoveNextAsync() && stream.Current == 1,
            "budget pressure must not abort the already accepted stream");
        Ensure(await stream.MoveNextAsync() && stream.Current == 2,
            "retired stream must remain bound through its final item");
        Ensure(!await stream.MoveNextAsync(), "retired stream completion after replacement");
        await WaitUntilAsync(async () => await service.GetEndpointIdAsync() == "second", TimeSpan.FromSeconds(3));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 1,
            "replacement must resume once the excess retiring stream has drained");
    }

    [Test]
    [NotInParallel]
    public async Task DynamicReconnectShouldProbeHealthyEndpointsAfterAFailingEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("bad", 1, "red"),
            Endpoint("first", first.Port, "green"),
            Endpoint("second", second.Port, "blue")
        ]));
        var failing = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, endpoint => endpoint.Id == "bad" ? failing : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));

        Ensure(failing.ConnectCount != 0, "the failing endpoint should have been probed");
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2,
            "a persistently failing endpoint must not monopolize the remaining ready target");
    }

    [Test]
    [NotInParallel]
    public async Task DynamicStopShouldWaitForAnInitialConnectThatIgnoresCancellation()
    {
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("blocked", 1, "red")]));
        var blocking = new BlockingConnectFactory();
        var client = SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, _ => blocking)
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            var stop = client.StopAsync().AsTask();
            await Task.Delay(100);
            Ensure(!stop.IsCompleted, "dynamic stop must wait for the initial connect worker");

            blocking.Release();
            await CaptureCancellation(connect);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(((SharpLinkClient)client).State == SharpLinkConnectionState.Stopped,
                "dynamic cluster must stop after the initial connect worker exits");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ConnectAfterDynamicClusterDisconnectShouldAwaitRecovery()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("unavailable", 1, "red")
        ]));
        var sockets = SharpLinkTransportFactories.Sockets();
        var blocking = new BlockAfterFirstConnectFactory(sockets(Endpoint("first", first.Port, "blue")));
        var unavailable = new FailingConnectFactory();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, endpoint => endpoint.Id == "first" ? blocking : unavailable)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await first.StopAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 0, TimeSpan.FromSeconds(3));
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        var reconnect = client.ConnectAsync().AsTask();
        var completed = await Task.WhenAny(reconnect, Task.Delay(100));
        Ensure(!ReferenceEquals(completed, reconnect),
            "dynamic ConnectAsync must wait for a new ready connection instead of returning startup success");
    }

    [Test]
    [NotInParallel]
    public async Task InitialDynamicDialReservationsShouldPreventSurplusTargetFill()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("blocked", 1, "green"),
            Endpoint("surplus", 2, "red")
        ]));
        var blocking = new BlockingConnectFactory();
        var surplus = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpointResolver(resolver, endpoint => endpoint.Id switch
            {
                "first" => sockets(endpoint),
                "blocked" => blocking,
                _ => surplus
            })
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await connect.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(200);

            Ensure(surplus.ConnectCount == 0,
                "a pending current-generation initial dial must reserve the remaining ready target");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
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

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task CaptureCancellation(Task task)
    {
        try
        {
            await task;
            throw new Exception("expected cancellation");
        }
        catch (OperationCanceledException)
        {
        }
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

    private sealed class FailingConnectFactory : IClientTransportFactory
    {
        private int _connectCount;
        private int _disposeCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return ValueTask.FromException<ITransportConnection>(new InvalidOperationException("test transport failure"));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceConnectFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _connectCount) == 1
                ? ValueTask.FromException<ITransportConnection>(new InvalidOperationException("test first dynamic dial failure"))
                : inner.ConnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BlockingConnectFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            return AwaitReleaseAsync();
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async ValueTask<ITransportConnection> AwaitReleaseAsync()
        {
            await _release.Task.ConfigureAwait(false);
            throw new OperationCanceledException();
        }
    }

    private sealed class BlockAfterFirstConnectFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public Task Entered => _entered.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) == 1)
                return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);

            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ThrowingDisposeFactory : IClientTransportFactory
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new InvalidOperationException("test transport failure"));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.FromException(new InvalidOperationException("test disposal failure"));
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
            for (var index = 0; index < context.Count; index++)
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
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

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            await _server.StopAsync(TimeSpan.Zero);
            await _cancellation.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(1000));
        }

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
            await StopAsync();
            await _server.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
