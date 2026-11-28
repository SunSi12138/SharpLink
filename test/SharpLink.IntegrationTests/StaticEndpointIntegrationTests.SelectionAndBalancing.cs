namespace SharpLink.IntegrationTests;

public sealed partial class StaticEndpointIntegrationTests
{
    [Test]
    public async Task InvalidCustomSelectorShouldFailOnlyTheCurrentCall()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new InvalidSelector())
            .Build();

        await client.ConnectAsync();
        var service = (await PrepareBalancedProxyAsync(client)).Service;
        try
        {
            _ = await service.PingAsync(1);
            throw new Exception("invalid selector should fail the current call");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.FailedPrecondition)
        {
        }
    }

    [Test]
    public async Task ThrowingCustomSelectorShouldLeaveTheClusterHealthyForLaterCalls()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var sockets = SharpLinkTransportFactories.Sockets();
        var gatedFirst = new GatedConnectFactory(sockets(Endpoint("first", first.Port)));
        var selector = new ThrowOnceSelector();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                endpoint => endpoint.Id == "first" ? gatedFirst : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseEndpointSelector(selector)
            .Build();

        try
        {
            var connecting = client.ConnectAsync().AsTask();
            await gatedFirst.Entered.WaitAsync(TimeSpan.FromSeconds(4));
            await connecting.WaitAsync(TimeSpan.FromSeconds(4));
            var service = (await PrepareBalancedProxyAsync(client)).Service;
            Ensure(first.OpenBindings == 0 && second.OpenBindings == 1,
                "only the initially Ready endpoint is prepared");

            gatedFirst.Release();
            await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            var exception = await EnsureThrowsSharpLink(service.PingAsync(1).AsTask(), "throwing custom selector");
            Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition &&
                   ReferenceEquals(exception.InnerException, selector.Failure), "the first RPC fails with the actual selector exception");
            Ensure(await service.PingAsync(1) == 2 && await service.GetEndpointIdAsync() == "second" &&
                   first.OpenBindings == 0 && second.OpenBindings == 1,
                "later RPC uses the original prepared endpoint without implicitly binding the newly Ready endpoint");
            Ensure(client.State == SharpLinkConnectionState.Ready, "selector failure must not change client state");
        }
        finally
        {
            gatedFirst.Release();
        }
    }

    [Test]
    public Task StaticClusterShouldExpandWithinGlobalAndPerEndpointBudgets()
        => VerifyClusterExpansionAsync(useResolver: false);

    [Test]
    public Task ResolverClusterShouldExpandWithinGlobalAndPerEndpointBudgets()
        => VerifyClusterExpansionAsync(useResolver: true);

    private static async Task VerifyClusterExpansionAsync(bool useResolver)
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var endpoints = new[] { Endpoint("first", first.Port), Endpoint("second", second.Port) };
        var builder = SharpClientBuilder.Create().DisableRequestTimeout();
        if (useResolver)
        {
            var snapshot = new SharpLinkEndpointSnapshot(1, endpoints);
            builder.UseEndpointResolver(new DelegateSharpLinkEndpointResolver(_ => ValueTask.FromResult(snapshot)),
                SharpLinkTransportFactories.Sockets());
        }
        else
        {
            builder.UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets());
        }
        await using var client = builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 2;
            })
            .Build();

        await client.ConnectAsync();
        EnsureReadiness(
            await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(2)),
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 2,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "initial two-endpoint pool readiness");
        var proxy = await PrepareBalancedProxyAsync(client);
        Ensure(first.OpenBindings == 1 && second.OpenBindings == 1,
            "initial readiness and preparation give exactly one prepared Session per endpoint");
        var service = proxy.Service;
        var calls = new Task<int>[32];
        for (var index = 0; index < calls.Length; index++)
            calls[index] = service.SlowAsync(100, CancellationToken.None).AsTask();
        Ensure((await Task.WhenAll(calls)).All(static result => result == 100),
            "overlapping calls finish on the prepared Sessions");

        var implementation = (SharpLinkClient)client;
        await WaitUntilAsync(
            () => client.GetReadinessSnapshot().ReadyConnections == 4,
            TimeSpan.FromSeconds(10));
        Ensure(implementation.ReadyConnectionCount == 4,
            $"cluster should fill only the configured global budget; observed {implementation.ReadyConnectionCount}");
        EnsureReadiness(
            client.GetReadinessSnapshot(),
            SharpLinkConnectionState.Ready,
            activeEndpoints: 2,
            readyEndpoints: 2,
            readyConnections: 4,
            targetReadyEndpoints: 2,
            meetsTarget: true,
            "expanded connection pool readiness");
        Ensure(await service.PingAsync(23) == 24 && first.OpenBindings == 1 && second.OpenBindings == 1,
            "the additional Ready Sessions carry no business until explicitly prepared");
        await proxy.PrepareAsync(TimeSpan.FromSeconds(4));
        Ensure(first.OpenBindings == 2 && second.OpenBindings == 2 && await service.PingAsync(29) == 30,
            "one explicit Prepare includes both newly Ready Sessions within the configured budgets");
    }

    [Test]
    [NotInParallel]
    public async Task CustomStaticSelectorShouldRejectTheOnlyNonMatchingReadyEndpoint()
    {
        await using var east = await TcpServerScope.StartAsync("east");
        await using var west = await TcpServerScope.StartAsync("west");
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("east", east.Port, "east"), Endpoint("west", west.Port, "west")],
                SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new AttributeSelector("west"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        await west.StopAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(2));

        var service = (await PrepareBalancedProxyAsync(client)).Service;
        var exception = await EnsureThrowsSharpLink(
            service.PingAsync(1).AsTask(),
            "selector must reject the only non-matching static endpoint");
        Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition,
            "a strict static selector must not be bypassed for one candidate");
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

        await using (var roundRobin = SharpClientBuilder.Create().DisableRequestTimeout()

                         .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
                         .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
                         .Build())
        {
            await roundRobin.ConnectAsync();
            await WaitUntilAsync(() => ((SharpLinkClient)roundRobin).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
            Ensure(((SharpLinkClient)roundRobin).ReadyConnectionCount == 2, "round robin endpoints must both be ready");
            var service = (await PrepareBalancedProxyAsync(roundRobin)).Service;
            var ids = new[]
            {
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync(),
                await service.GetEndpointIdAsync()
            };
            Ensure(ids[0] != ids[1] && ids[0] == ids[2] && ids[1] == ids[3], "round robin endpoint order");
        }

        await using var custom = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new AttributeSelector("west"))
            .Build();
        await custom.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)custom).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)custom).ReadyConnectionCount == 2, "custom selector endpoints must both be ready");
        var customService = (await PrepareBalancedProxyAsync(custom)).Service;
        Ensure(await customService.GetEndpointIdAsync() == "west", "custom selector attributes");
    }

    [Test]
    public async Task LeastPendingShouldAvoidEndpointWithAnActiveCall()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending)
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2, "least-pending endpoints must both be ready");
        var service = (await PrepareBalancedProxyAsync(client)).Service;
        var slow = service.SlowAsync(200, CancellationToken.None).AsTask();
        var completed = await Task.WhenAny(first.Service.SlowCallStarted!.Task, second.Service.SlowCallStarted!.Task);
        var busyId = await ((Task<string>)completed);
        var selectedId = await service.GetEndpointIdAsync();
        Ensure(selectedId != busyId, "least pending should select the non-busy endpoint");
        await slow;
    }

    [Test]
    public async Task LeastPendingShouldRotateTiesAcrossReadyEndpoints()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending)
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2, "least-pending endpoints must both be ready");
        var service = (await PrepareBalancedProxyAsync(client)).Service;
        var ids = new[]
        {
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync()
        };
        Ensure(ids[0] != ids[1] && ids[0] == ids[2] && ids[1] == ids[3], "least-pending tie rotation");
    }
}
