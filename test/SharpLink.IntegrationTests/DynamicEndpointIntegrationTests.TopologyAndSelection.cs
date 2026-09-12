using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SharpLink.IntegrationTests;

public sealed partial class DynamicEndpointIntegrationTests
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
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
    public async Task DynamicReadinessShouldTrackTopologyChangesAndKeepWaiterCancellationLocal()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var third = await TcpServerScope.StartAsync("third");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green")
        ]));
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new IdSelector("third"))
            .UseCluster(options =>
            {
                options.MaxEndpoints = 3;
                options.MinReadyEndpoints = 3;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        var initial = await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            initial,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "initial two-endpoint topology");

        using var canceledWaitCancellation = new CancellationTokenSource();
        var canceledWait = client.WaitForReadinessAsync(3, canceledWaitCancellation.Token).AsTask();
        var survivingWait = client.WaitForReadinessAsync(3).AsTask();
        Ensure(!canceledWait.IsCompleted && !survivingWait.IsCompleted,
            "configured MinReadyEndpoints=3 must allow waits to remain pending while only two endpoints exist");

        canceledWaitCancellation.Cancel();
        await CaptureCancellation(canceledWait.WaitAsync(TimeSpan.FromSeconds(3)));
        Ensure(!survivingWait.IsCompleted, "canceling one readiness waiter must not cancel another waiter");
        EnsureReadiness(
            client.GetReadinessSnapshot(),
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "topology after local waiter cancellation");

        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(2,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green"),
            Endpoint("third", third.Port, "red")
        ]));
        var added = await survivingWait.WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            added,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 3,
            readyEndpoints: 3,
            readyConnections: 3,
            targetReadyEndpoints: 3,
            meetsTarget: true,
            "two-to-three endpoint addition");

        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(
            3,
            [Endpoint("first", first.Port, "blue")]));
        var removed = await client.WaitForReadinessAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            removed,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 1,
            readyEndpoints: 1,
            readyConnections: 1,
            targetReadyEndpoints: 1,
            meetsTarget: true,
            "three-to-one endpoint removal");

        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(4,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green")
        ]));
        var beforeReplacement = await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            beforeReplacement,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "A/B topology before replacement");

        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(5,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("third", third.Port, "red")
        ]));
        var replacement = await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            replacement,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "A/C replacement topology");
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "third",
            "the replacement topology must route to the new C endpoint generation");

        var lastAccepted = client.GetReadinessSnapshot();
        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(4,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green")
        ]));
        Ensure(client.GetReadinessSnapshot() == lastAccepted,
            "a stale resolver snapshot must leave readiness facts unchanged");
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "third",
            "a stale resolver snapshot must not restore the retired B endpoint");

        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(6,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green"),
            Endpoint("third", third.Port, "red"),
            Endpoint("overflow", third.Port, "yellow")
        ]));
        Ensure(client.GetReadinessSnapshot() == lastAccepted,
            "a rejected resolver snapshot must leave readiness facts unchanged");
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "third",
            "a rejected resolver snapshot must retain the last accepted topology");
    }

    [Test]
    [NotInParallel]
    public async Task EmptyDynamicTopologyShouldRecoverWhenTheResolverPublishesAnEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1, []));
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        EnsureReadiness(
            client.GetReadinessSnapshot(),
            SharpLinkConnectionState.Reconnecting,
            activeEndpoints: 0,
            readyEndpoints: 0,
            readyConnections: 0,
            targetReadyEndpoints: 0,
            meetsTarget: false,
            "accepted empty topology");
        var repeatedConnect = client.ConnectAsync();
        Ensure(repeatedConnect.IsCompletedSuccessfully,
            "repeated ConnectAsync on an accepted empty topology must complete without waiting for recovery");
        await repeatedConnect;

        var readiness = client.WaitForReadinessAsync(2).AsTask();
        Ensure(!readiness.IsCompleted, "readiness wait must remain pending while the accepted topology is empty");
        await resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(2,
        [
            Endpoint("first", first.Port, "blue"),
            Endpoint("second", second.Port, "green")
        ]));
        var recovered = await readiness.WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            recovered,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "empty-to-two endpoint recovery");
        var endpointId = await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync();
        Ensure(endpointId is "first" or "second", "topology recovery RPC");
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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new IdSelector("first"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        var initial = await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        EnsureReadiness(
            initial,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "two active endpoints before retirement");
        var service = client.Get<IConnectionBehaviorService>();
        await using var stream = service.SlowRangeAsync(3, 500, CancellationToken.None).GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync() && stream.Current == 0, "first stream item");

        await resolver.PublishAndWaitAsync(
            new SharpLinkEndpointSnapshot(2, [Endpoint("second", second.Port, "green")]));
        var retired = await client.WaitForReadinessAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(((SharpLinkClient)client).ActiveClientStreamCount == 1,
            "the removed endpoint generation must still be draining its accepted stream");
        EnsureReadiness(
            retired,
            SharpLinkConnectionState.Ready,
            activeEndpoints: 1,
            readyEndpoints: 1,
            readyConnections: 1,
            targetReadyEndpoints: 1,
            meetsTarget: true,
            "retired old generation excluded while draining");
        Ensure(await service.GetEndpointIdAsync() == "second", "new call after endpoint removal");
        Ensure(await stream.MoveNextAsync() && stream.Current == 1, "draining stream second item");
        Ensure(await stream.MoveNextAsync() && stream.Current == 2, "draining stream third item");
        Ensure(!await stream.MoveNextAsync(), "draining stream completion");
    }

    [Test]
    [NotInParallel]
    public async Task StaleDynamicSelectionShouldNotRecreateRetiredAdmissionState()
    {
        await using var server = await TcpServerScope.StartAsync("retiring");
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("retiring", server.Port, "blue")]));
        using var selector = new PausingSelector();
        var admission = new TrackingLifecycleAdmissionPolicy();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(selector)
            .UseEndpointAdmission(admission)
            .Build();

        await client.ConnectAsync();
        var call = Task.Run(async () =>
            await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync());
        await selector.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            resolver.Publish(new SharpLinkEndpointSnapshot(2, []));
            await WaitUntilAsync(
                () => admission.RetireCount == 1 && ((SharpLinkClient)client).ReadyConnectionCount == 0,
                TimeSpan.FromSeconds(3));

            selector.Release();
            var exception = await CaptureSharpLinkException(call.WaitAsync(TimeSpan.FromSeconds(3)));
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "stale selection failure code");
            Ensure(admission.ActiveGenerationCount == 0,
                "a stale selection must not recreate state after its endpoint generation has retired");
        }
        finally
        {
            selector.Release();
        }
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
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

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
}
