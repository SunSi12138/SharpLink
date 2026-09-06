using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SharpLink.IntegrationTests;

public sealed partial class DynamicEndpointIntegrationTests
{







    [Test]
    [NotInParallel]
    public async Task FailedInitialDynamicTopologyShouldAllowConnectToWaitForRecovery()
    {
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("failed", 1, "red")]));
        var factory = new FailingConnectFactory();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        try
        {
            var first = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
            using var recoveryCancellation = new CancellationTokenSource();
            var recovery = client.ConnectAsync(recoveryCancellation.Token).AsTask();
            await Task.Delay(20);

            Ensure(first.Code == SharpLinkErrorCode.Unavailable, "initial failed dynamic topology error");
            Ensure(!recovery.IsCompleted,
                "failed initial dynamic topology must wait for recovery instead of replaying the stale failure");
            recoveryCancellation.Cancel();
            await CaptureCancellation(recovery.WaitAsync(TimeSpan.FromSeconds(5)));
            Ensure(factory.ConnectCount != 0, "resolved endpoint connection must have been attempted");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task FailedInitialDynamicDialShouldProbeLaterEndpointsWithoutWaitingForASlowSibling()
    {
        await using var healthy = await TcpServerScope.StartAsync("healthy");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
            1,
            [Endpoint("failed", 1, "red"), Endpoint("blocked", 2, "red"), Endpoint("healthy", healthy.Port, "green")]));
        var blocking = new BlockingConnectFactory();
        var failing = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpointResolver(resolver, endpoint => endpoint.Id switch
            {
                "failed" => failing,
                "blocked" => blocking,
                _ => sockets(endpoint)
            })
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            await connect.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(((SharpLinkClient)client).ReadyConnectionCount == 1,
                "a failed resolver-backed initial dial must immediately probe a later healthy endpoint");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task DynamicRecoveryToAnEmptyTopologyShouldReleaseConnectWaiters()
    {
        var resolver = new FailingThenEmptyResolver();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, _ => new FailingConnectFactory())
            .Build();

        var initial = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
        Ensure(initial.Code == SharpLinkErrorCode.Unavailable, "initial resolver failure error code");
        await resolver.EmptyResolveStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var recovery = client.ConnectAsync().AsTask();
        await Task.Delay(20);
        Ensure(!recovery.IsCompleted, "ConnectAsync must wait for the resolver recovery result");

        resolver.ReleaseEmptyTopology();
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 0,
            "an accepted empty topology completes recovery without fabricating a ready connection");
    }

    [Test]
    [NotInParallel]
    public async Task FailedInitialDynamicDialShouldReconnectWithoutANewerResolverVersion()
    {
        await using var server = await TcpServerScope.StartAsync("recovered");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("recovered", server.Port, "green")]));
        var factory = new FailOnceConnectFactory(SharpLinkTransportFactories.Sockets()(Endpoint("recovered", server.Port, "green")));
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        var initial = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
        Ensure(initial.Code == SharpLinkErrorCode.Unavailable, "initial failed dial reports unavailable");

        var recovery = client.ConnectAsync().AsTask();
        var completed = await Task.WhenAny(recovery, Task.Delay(20));
        Ensure(!ReferenceEquals(completed, recovery),
            "repeated ConnectAsync must wait for recovery instead of replaying the initial dial failure");
        await recovery.WaitAsync(TimeSpan.FromSeconds(3));
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
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, _ => blocking)
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            var readiness = client.WaitForReadinessAsync(1).AsTask();

            var stop = client.StopAsync().AsTask();
            await Task.Delay(100);
            Ensure(!stop.IsCompleted, "dynamic stop must wait for the initial connect worker");

            blocking.Release();
            await CaptureCancellation(connect);
            var readinessFailure = await CaptureSharpLinkException(readiness);
            Ensure(readinessFailure.Code == SharpLinkErrorCode.ConnectionClosed,
                "dynamic readiness must map the joined Connect shutdown result to ConnectionClosed");
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
    public async Task InitialDynamicConnectShouldCompleteWhenAReplacementTopologyBecomesReady()
    {
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("blocked", 1, "red")]));
        var blocking = new BlockingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpointResolver(resolver, endpoint => endpoint.Id == "blocked" ? blocking : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var initial = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("replacement", replacement.Port, "green")]));

            await initial.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "replacement",
                "initial ConnectAsync must observe a ready replacement instead of waiting for a retired dial");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task RetiredDynamicDialsShouldContinueToConsumeTheConnectionBudget()
    {
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("blocked", 1, "red")]));
        var blocking = new BlockingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var replacementFactory = new CountingConnectFactory(sockets(Endpoint("replacement", replacement.Port, "green")));
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpointResolver(resolver, endpoint => endpoint.Id == "blocked" ? blocking : replacementFactory)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var initial = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("replacement", replacement.Port, "green")]));
            await Task.Delay(100);

            Ensure(replacementFactory.ConnectCount == 0,
                "a retired in-flight dial must keep the sole connection budget reserved");

            blocking.Release();
            var initialFailure = await CaptureSharpLinkException(initial);
            Ensure(initialFailure.Code == SharpLinkErrorCode.Unavailable, "retired initial dial failure result");
            await WaitUntilAsync(
                () => replacementFactory.ConnectCount == 1 && ((SharpLinkClient)client).ReadyConnectionCount == 1,
                TimeSpan.FromSeconds(3));
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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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

    private static void EnsureReadiness(
        SharpLinkClientReadinessSnapshot actual,
        SharpLinkConnectionState state,
        int activeEndpoints,
        int readyEndpoints,
        int readyConnections,
        int targetReadyEndpoints,
        bool meetsTarget,
        string scenario)
    {
        var expected = new SharpLinkClientReadinessSnapshot(
            state,
            activeEndpoints,
            readyEndpoints,
            readyConnections,
            targetReadyEndpoints);
        Ensure(actual == expected, $"{scenario}: expected {expected}, actual {actual}");
        Ensure(actual.MeetsTarget == meetsTarget,
            $"{scenario}: expected MeetsTarget={meetsTarget}, actual {actual.MeetsTarget}");
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
        private readonly Channel<ResolverUpdate> _snapshots = Channel.CreateUnbounded<ResolverUpdate>();
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var update in _snapshots.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update.Snapshot;
                update.Processed?.TrySetResult();
            }
        }

        public void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(new ResolverUpdate(snapshot, Processed: null));

        public async Task PublishAndWaitAsync(SharpLinkEndpointSnapshot snapshot)
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ensure(_snapshots.Writer.TryWrite(new ResolverUpdate(snapshot, processed)),
                "resolver update channel must accept the test snapshot");
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
                _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private readonly record struct ResolverUpdate(
            SharpLinkEndpointSnapshot Snapshot,
            TaskCompletionSource? Processed);
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

    private sealed class CountingConnectFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
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

    private sealed class PausingSelector : ISharpLinkEndpointSelector, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public Task Entered => _entered.Task;

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The paused endpoint selection was not released.");
            return 0;
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class TrackingLifecycleAdmissionPolicy :
        ISharpLinkEndpointAdmissionPolicy,
        ISharpLinkEndpointAdmissionLifecycle
    {
        private readonly ConcurrentDictionary<(string Id, long Generation), byte> _active = new();
        private int _retireCount;

        public int ActiveGenerationCount => _active.Count;
        public int RetireCount => Volatile.Read(ref _retireCount);

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            _active.TryAdd((endpoint.Endpoint.Id, endpoint.Generation), 0);
            return new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }

        public void Retire(in SharpLinkEndpointCandidate endpoint)
        {
            _active.TryRemove((endpoint.Endpoint.Id, endpoint.Generation), out _);
            Interlocked.Increment(ref _retireCount);
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

    private sealed class FailingThenEmptyResolver : ISharpLinkEndpointResolver
    {
        private readonly TaskCompletionSource _emptyResolveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseEmptyTopology =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resolveCount;

        public Task EmptyResolveStarted => _emptyResolveStarted.Task;

        public async ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _resolveCount) == 1)
                throw new InvalidOperationException("initial resolver failure");

            _emptyResolveStarted.TrySetResult();
            await _releaseEmptyTopology.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new SharpLinkEndpointSnapshot(1, []);
        }

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public void ReleaseEmptyTopology() => _releaseEmptyTopology.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _releaseEmptyTopology.TrySetResult();
            return ValueTask.CompletedTask;
        }
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
