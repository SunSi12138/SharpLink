namespace SharpLink.IntegrationTests;

public sealed partial class StaticEndpointIntegrationTests
{
    [Test]
    public async Task StaticReadinessCreatedSnapshotsShouldReflectConfiguredEndpointCounts()
    {
        await using var twoEndpointClient = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", 1), Endpoint("second", 2)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();
        await using var threeEndpointClient = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", 1), Endpoint("second", 2), Endpoint("third", 3)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 3;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        EnsureReadiness(
            twoEndpointClient.GetReadinessSnapshot(),
            SharpLinkConnectionState.Created,
            activeEndpoints: 2,
            readyEndpoints: 0,
            readyConnections: 0,
            targetReadyEndpoints: 2,
            meetsTarget: false,
            "two-endpoint Created readiness");
        EnsureReadiness(
            threeEndpointClient.GetReadinessSnapshot(),
            SharpLinkConnectionState.Created,
            activeEndpoints: 3,
            readyEndpoints: 0,
            readyConnections: 0,
            targetReadyEndpoints: 3,
            meetsTarget: false,
            "three-endpoint Created readiness");
    }

    [Test]
    [NotInParallel]
    public async Task StaticReadinessWaitsShouldNotChangeConnectAsyncConnectivityBoundary()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var sockets = SharpLinkTransportFactories.Sockets();
        var gatedSecond = new GatedConnectFactory(sockets(Endpoint("second", second.Port)));
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                endpoint => endpoint.Id == "second" ? gatedSecond : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await gatedSecond.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await connect.WaitAsync(TimeSpan.FromSeconds(2));

            EnsureReadiness(
                client.GetReadinessSnapshot(),
                SharpLinkConnectionState.Ready,
                activeEndpoints: 2,
                readyEndpoints: 1,
                readyConnections: 1,
                targetReadyEndpoints: 2,
                meetsTarget: false,
                "ConnectAsync first-connectivity readiness");
            EnsureReadiness(
                await client.WaitForReadinessAsync(1),
                SharpLinkConnectionState.Ready,
                activeEndpoints: 2,
                readyEndpoints: 1,
                readyConnections: 1,
                targetReadyEndpoints: 2,
                meetsTarget: false,
                "Wait(1) readiness");

            var waitForTwo = client.WaitForReadinessAsync(2).AsTask();
            Ensure(!waitForTwo.IsCompleted, "Wait(2) must remain pending while the second endpoint dial is gated");

            gatedSecond.Release();
            EnsureReadiness(
                await waitForTwo.WaitAsync(TimeSpan.FromSeconds(2)),
                SharpLinkConnectionState.Ready,
                activeEndpoints: 2,
                readyEndpoints: 2,
                readyConnections: 2,
                targetReadyEndpoints: 2,
                meetsTarget: true,
                "Wait(2) readiness");
        }
        finally
        {
            gatedSecond.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task StaticReadinessWaitBelowTargetShouldCompleteBeforeFullConvergence()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var third = await TcpServerScope.StartAsync("third");
        var sockets = SharpLinkTransportFactories.Sockets();
        var gatedThird = new GatedConnectFactory(sockets(Endpoint("third", third.Port)));
        var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [
                    Endpoint("first", first.Port),
                    Endpoint("second", second.Port),
                    Endpoint("third", third.Port)
                ],
                endpoint => endpoint.Id == "third" ? gatedThird : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 3;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await gatedThird.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await connect.WaitAsync(TimeSpan.FromSeconds(2));

            EnsureReadiness(
                await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(2)),
                SharpLinkConnectionState.Ready,
                activeEndpoints: 3,
                readyEndpoints: 2,
                readyConnections: 2,
                targetReadyEndpoints: 3,
                meetsTarget: false,
                "Wait(2) below configured target readiness");

            var waitForThree = client.WaitForReadinessAsync(3).AsTask();
            Ensure(!waitForThree.IsCompleted, "Wait(3) must remain pending until the third endpoint is ready");
            gatedThird.Release();
            EnsureReadiness(
                await waitForThree.WaitAsync(TimeSpan.FromSeconds(2)),
                SharpLinkConnectionState.Ready,
                activeEndpoints: 3,
                readyEndpoints: 3,
                readyConnections: 3,
                targetReadyEndpoints: 3,
                meetsTarget: true,
                "full static target readiness");
        }
        finally
        {
            gatedThird.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task StaticReadinessThresholdAboveConfiguredTargetShouldFailWithoutDialingAnotherEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var surplus = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [
                    Endpoint("first", first.Port),
                    Endpoint("second", second.Port),
                    Endpoint("surplus", 1)
                ],
                endpoint => endpoint.Id == "surplus" ? surplus : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(surplus.ConnectCount == 0, "the endpoint above the configured target must not be dialed");

        try
        {
            _ = client.WaitForReadinessAsync(3);
            throw new Exception("Wait(3) should reject a static target configured for two endpoints");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Ensure(exception.ParamName == "minimumReadyEndpoints", "static readiness threshold parameter name");
        }

        await Task.Yield();
        Ensure(surplus.ConnectCount == 0, "an invalid readiness wait must not trigger an extra endpoint dial");
    }
}
