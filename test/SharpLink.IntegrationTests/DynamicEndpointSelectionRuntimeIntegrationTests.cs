namespace SharpLink.IntegrationTests;

public sealed partial class DynamicEndpointIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task RuntimeSelectionPolicyShouldComposeWithDynamicTopologyReplacementWithoutExtraDial()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        await using var third = await TcpServerScope.StartAsync("third");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
            1,
            [Endpoint("first", first.Port, "blue"), Endpoint("second", second.Port, "green")]));
        var counter = new RuntimeSelectionConnectCounter();
        var sockets = SharpLinkTransportFactories.Sockets();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(
                resolver,
                endpoint => new RuntimeSelectionCountingTransportFactory(sockets(endpoint), counter))
            .UseEndpointSelector(new IdSelector("first"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
        Ensure(counter.Count == 2, "initial dynamic topology should dial exactly two Ready endpoints");

        var service = client.Get<IConnectionBehaviorService>();
        Ensure(await service.GetEndpointIdAsync() == "first", "initial dynamic selector should route to first");

        client.UpdateEndpointSelector(new IdSelector("second"));
        Ensure(await service.GetEndpointIdAsync() == "second",
            "a live dynamic client should route its next physical attempt with the new selector");
        Ensure(counter.Count == 2,
            "publishing a dynamic selection policy must not reconnect healthy sessions");

        await using var retiringStream = service
            .SlowRangeAsync(3, 100, CancellationToken.None)
            .GetAsyncEnumerator();
        Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 0,
            "the accepted stream should bind to the endpoint selected before retirement begins");

        var topologyUpdate = resolver.PublishAndWaitAsync(new SharpLinkEndpointSnapshot(
            2,
            [Endpoint("first", first.Port, "blue"), Endpoint("third", third.Port, "yellow")]));
        var policyUpdates = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 256; iteration++)
            {
                client.UpdateEndpointSelector(new IdSelector((iteration & 1) == 0 ? "first" : "third"));
            }
            client.UpdateEndpointSelector(new IdSelector("third"));
        });
        await Task.WhenAll(topologyUpdate, policyUpdates);

        Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 1,
            "policy/topology publication must not move an accepted stream off its retiring endpoint");
        Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 2,
            "the retiring endpoint must remain bound through the accepted stream's final item");
        Ensure(!await retiringStream.MoveNextAsync(),
            "the accepted stream should complete normally while its endpoint drains");

        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
        await WaitUntilAsync(
            async () => await service.GetEndpointIdAsync() == "third",
            TimeSpan.FromSeconds(3));
        Ensure(counter.Count == 3,
            "the resolver replacement should require exactly one new dial; policy publications must add none");
        Ensure(client.GetReadinessSnapshot().ActiveEndpoints == 2,
            "selection publication must not alter resolver-owned endpoint membership");
    }

    private sealed class RuntimeSelectionConnectCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class RuntimeSelectionCountingTransportFactory(
        IClientTransportFactory inner,
        RuntimeSelectionConnectCounter counter) : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
