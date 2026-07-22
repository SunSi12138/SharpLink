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
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(2));
        Ensure(await service.PingAsync(8) == 9, "remaining endpoint should serve RPC");
    }

    [Test]
    public async Task InitialEndpointFailureShouldNotPreventAnotherEndpointFromConnecting()
    {
        using var unavailableListener = new TcpListener(IPAddress.Loopback, 0);
        unavailableListener.Start();
        var unavailablePort = ((IPEndPoint)unavailableListener.LocalEndpoint).Port;
        unavailableListener.Stop();

        await using var available = await TcpServerScope.StartAsync("available");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("unavailable", unavailablePort), Endpoint("available", available.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() == "available",
            "a healthy endpoint should connect when another is unavailable");
    }

    [Test]
    public async Task AllUnavailableEndpointsShouldReportUnavailable()
    {
        var firstPort = GetUnusedTcpPort();
        var secondPort = GetUnusedTcpPort();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", firstPort), Endpoint("second", secondPort)],
                SharpLinkTransportFactories.Sockets())
            .Build();

        var exception = await EnsureThrowsSharpLink(client.ConnectAsync().AsTask(), "all unavailable endpoints");
        Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "all unavailable endpoints error code");
    }

    [Test]
    [NotInParallel]
    public async Task DisconnectedEndpointShouldReconnectWithoutInterruptingAnotherEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseEndpointSelector(new PreferEndpointSelector("first"))
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        Ensure(await service.GetEndpointIdAsync() == "first", "initial preferred endpoint");

        var port = first.Port;
        await first.StopAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1, TimeSpan.FromSeconds(2));
        Ensure(await service.GetEndpointIdAsync() == "second", "healthy endpoint remains available during reconnect");

        await using var replacement = await TcpServerScope.StartAsync("first-reconnected", port);
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2, "disconnected endpoint should reconnect independently");
        Ensure(await service.GetEndpointIdAsync() == "first-reconnected", "reconnected endpoint should rejoin selection");
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
    public async Task ThrowingCustomSelectorShouldLeaveTheClusterHealthyForLaterCalls()
    {
        await using var first = await TcpServerScope.StartAsync();
        await using var second = await TcpServerScope.StartAsync();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(new ThrowOnceSelector())
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        var exception = await EnsureThrowsSharpLink(service.PingAsync(1).AsTask(), "throwing custom selector");
        Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition, "throwing selector error code");
        Ensure(await service.PingAsync(1) == 2, "later RPC should remain healthy");
        Ensure(client.State == SharpLinkConnectionState.Ready, "selector failure must not change client state");
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
    public async Task StaticTcpEndpointsShouldSupportHostnameIpv4AndIpv6()
    {
        await using var hostname = await TcpServerScope.StartAsync("hostname");
        await using var ipv4 = await TcpServerScope.StartAsync("ipv4");
        await using var ipv6 = Socket.OSSupportsIPv6
            ? await TcpServerScope.StartAsync("ipv6", address: IPAddress.IPv6Loopback)
            : null;
        var endpoints = new List<SharpLinkEndpoint>
        {
            new()
            {
                Id = "hostname",
                Address = new SharpLinkTcpAddress("localhost", hostname.Port)
            },
            Endpoint("ipv4", ipv4.Port)
        };
        if (ipv6 is not null)
        {
            endpoints.Add(new SharpLinkEndpoint
            {
                Id = "ipv6",
                Address = new SharpLinkTcpAddress(IPAddress.IPv6Loopback.ToString(), ipv6.Port)
            });
        }

        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = endpoints.Count;
                options.MaxConnections = endpoints.Count;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(
            () => ((SharpLinkClient)client).ReadyConnectionCount == endpoints.Count,
            TimeSpan.FromSeconds(3));
        var service = client.Get<IConnectionBehaviorService>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < endpoints.Count * 2; index++)
            observed.Add(await service.GetEndpointIdAsync());
        Ensure(observed.Contains("hostname") && observed.Contains("ipv4"), "hostname and IPv4 endpoints");
        if (ipv6 is not null)
            Ensure(observed.Contains("ipv6"), "IPv6 endpoint");
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
    [NotInParallel]
    public async Task StopShouldWaitForInitialSiblingDialsBeforeDisposingFactories()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var blocking = new BlockingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("blocked", GetUnusedTcpPort())],
                endpoint => endpoint.Id == "first" ? sockets(endpoint) : blocking)
            .Build();

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await connect.WaitAsync(TimeSpan.FromSeconds(2));

            var stop = client.StopAsync().AsTask();
            await Task.Delay(100);
            Ensure(!stop.IsCompleted, "StopAsync must wait for an in-flight initial sibling dial");

            blocking.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(((SharpLinkClient)client).State == SharpLinkConnectionState.Stopped,
                "cluster must stop after the sibling dial finishes");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task InitialStaticDialReservationsShouldPreventSurplusTargetFill()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var blocking = new BlockingConnectFactory();
        var surplus = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("blocked", 1), Endpoint("surplus", 2)],
                endpoint => endpoint.Id switch
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
                "a pending initial sibling must reserve the remaining ready target before reconciliation");
        }
        finally
        {
            blocking.Release();
            await client.DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ConnectAfterStaticClusterDisconnectShouldAwaitRecovery()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        var sockets = SharpLinkTransportFactories.Sockets();
        var blocking = new BlockAfterFirstConnectFactory(sockets(Endpoint("first", first.Port)));
        var unavailable = new FailingConnectFactory();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("unavailable", 1)],
                endpoint => endpoint.Id == "first" ? blocking : unavailable)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await first.StopAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 0, TimeSpan.FromSeconds(2));
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        var reconnect = client.ConnectAsync().AsTask();
        var completed = await Task.WhenAny(reconnect, Task.Delay(100));
        Ensure(!ReferenceEquals(completed, reconnect),
            "ConnectAsync must wait for a new ready connection instead of returning stale initialization success");
    }

    [Test]
    [NotInParallel]
    public async Task FailedInitialSiblingDialShouldContinueFillingMinReadyEndpoints()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var recovered = await TcpServerScope.StartAsync("recovered");
        var sockets = SharpLinkTransportFactories.Sockets();
        var delayedFailure = new DeferredFailOnceFactory(sockets(Endpoint("recovered", recovered.Port)));
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("recovered", recovered.Port)],
                endpoint => endpoint.Id == "recovered" ? delayedFailure : sockets(endpoint))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        var connect = client.ConnectAsync().AsTask();
        await delayedFailure.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        await connect.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 1,
            "first endpoint should make initial connect available before the sibling finishes");

        delayedFailure.FailInitialConnect();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
        Ensure(delayedFailure.ConnectCount >= 2,
            "failed initial sibling must be retried to restore the configured minimum ready count");
        Ensure(await client.Get<IConnectionBehaviorService>().GetEndpointIdAsync() is "first" or "recovered",
            "recovered static endpoint topology should remain usable");
    }

    [Test]
    [NotInParallel]
    public async Task StaticReconnectShouldProbeHealthyEndpointsAfterAFailingEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var failing = new FailingConnectFactory();
        var sockets = SharpLinkTransportFactories.Sockets();
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("bad", 1), Endpoint("first", first.Port), Endpoint("second", second.Port)],
                endpoint => endpoint.Id == "bad" ? failing : sockets(endpoint))
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
            "a persistently failing endpoint must not monopolize the static ready target");
    }

    [Test]
    [NotInParallel]
    public async Task InitialStaticConnectShouldContinueFillingTargetsBeyondTheFirstBatch()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var third = await TcpServerScope.StartAsync("third");
        await using var fourth = await TcpServerScope.StartAsync("fourth");
        await using var fifth = await TcpServerScope.StartAsync("fifth");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [
                    Endpoint("first", first.Port),
                    Endpoint("second", second.Port),
                    Endpoint("third", third.Port),
                    Endpoint("fourth", fourth.Port),
                    Endpoint("fifth", fifth.Port)
                ],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 5;
                options.MaxConnections = 5;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 5, TimeSpan.FromSeconds(3));

        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 5,
            "initial static connect must continue filling endpoints beyond its first parallel batch");
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
            await WaitUntilAsync(() => ((SharpLinkClient)roundRobin).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
            Ensure(((SharpLinkClient)roundRobin).ReadyConnectionCount == 2, "round robin endpoints must both be ready");
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
        await WaitUntilAsync(() => ((SharpLinkClient)custom).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        Ensure(((SharpLinkClient)custom).ReadyConnectionCount == 2, "custom selector endpoints must both be ready");
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

    [Test]
    public async Task LeastPendingShouldRotateTiesAcrossReadyEndpoints()
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
        var ids = new[]
        {
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync(),
            await service.GetEndpointIdAsync()
        };
        Ensure(ids[0] != ids[1] && ids[0] == ids[2] && ids[1] == ids[3], "least-pending tie rotation");
    }

    [Test]
    [NotInParallel]
    public async Task GoAwayShouldDrainExistingUnaryAndStreamWhileNewCallsUseAnotherEndpoint()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var client = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseEndpointSelector(new PreferEndpointSelector("first"))
            .Build();

        await client.ConnectAsync();
        var service = client.Get<IConnectionBehaviorService>();
        var longUnary = service.SlowAsync(600, CancellationToken.None).AsTask();
        Ensure(await first.Service.SlowUnaryStarted!.Task == "first", "accepted unary should start on first endpoint");
        await using var stream = service.SlowRangeAsync(3, 100, CancellationToken.None).GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync() && stream.Current == 0, "first stream item");
        Ensure(await first.Service.SlowCallStarted!.Task == "first", "existing stream should start on first endpoint");

        var stopTask = first.StopAsync(TimeSpan.FromSeconds(2)).AsTask();
        var implementation = (SharpLinkClient)client;
        await WaitUntilAsync(() => implementation.ReadyConnectionCount == 1, TimeSpan.FromSeconds(2));
        Ensure(implementation.ReadyConnectionCount == 1, "GoAway should retire the draining endpoint from selection");
        Ensure(await service.GetEndpointIdAsync() == "second", "new RPC should use the remaining endpoint");

        Ensure(await stream.MoveNextAsync() && stream.Current == 1, "draining stream second item");
        Ensure(await stream.MoveNextAsync() && stream.Current == 2, "draining stream third item");
        Ensure(!await stream.MoveNextAsync(), "draining stream completion");
        Ensure(await longUnary == 600, "accepted unary should complete during graceful drain");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static SharpLinkEndpoint Endpoint(string id, int port, string? zone = null) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port),
        Attributes = zone is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["zone"] = zone }
    };

    private static int GetUnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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

    private static async Task<SharpLinkException> EnsureThrowsSharpLink(Task action, string name)
    {
        try
        {
            await action;
            throw new Exception($"{name} should throw SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
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

        public static Task<TcpServerScope> StartAsync(
            string endpointId = "default",
            int port = 0,
            IPAddress? address = null)
        {
            var listenAddress = address ?? IPAddress.Loopback;
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(port, listenAddress.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            var service = new ConnectionBehaviorService
            {
                EndpointId = endpointId,
                SlowCallStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                SlowUnaryStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            builder.ReplaceService<IConnectionBehaviorService>(service);
            var boundPort = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new TcpServerScope(builder.Build(), boundPort, service));
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

        public async ValueTask StopAsync(TimeSpan gracefulTimeout = default)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            await _server.StopAsync(gracefulTimeout);
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
            throw new InvalidOperationException("test initial dial completed after release");
        }
    }

    private sealed class DeferredFailOnceFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public Task Entered => _entered.Task;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) != 1)
                return inner.ConnectAsync(cancellationToken);
            _entered.TrySetResult();
            return FailAfterReleaseAsync();
        }

        public void FailInitialConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async ValueTask<ITransportConnection> FailAfterReleaseAsync()
        {
            await _release.Task.ConfigureAwait(false);
            throw new InvalidOperationException("test initial sibling failure");
        }
    }

    private sealed class FailingConnectFactory : IClientTransportFactory
    {
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return ValueTask.FromException<ITransportConnection>(new InvalidOperationException("test transport failure"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class ThrowOnceSelector : ISharpLinkEndpointSelector
    {
        private int _remainingFailures = 1;

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
                throw new InvalidOperationException("test selector failure");
            return 0;
        }
    }

    private sealed class PreferEndpointSelector(string endpointId) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) == 0 &&
                    context[index].Endpoint.Id == endpointId)
                {
                    return index;
                }
            }

            for (var index = 0; index < context.Count; index++)
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
            return -1;
        }
    }
}
