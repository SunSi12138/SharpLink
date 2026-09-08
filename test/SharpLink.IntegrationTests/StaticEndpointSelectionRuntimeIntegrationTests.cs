namespace SharpLink.IntegrationTests;

public sealed partial class StaticEndpointIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task RuntimeSelectionPolicyShouldRerouteReadyStaticClusterWithoutReconnect()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var second = await TcpServerScope.StartAsync("second");
        var counter = new RuntimeSelectionConnectCounter();
        var sockets = SharpLinkTransportFactories.Sockets();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpoints(
                [Endpoint("first", first.Port), Endpoint("second", second.Port)],
                endpoint => new RuntimeSelectionCountingTransportFactory(sockets(endpoint), counter))
            .UseEndpointSelector(new PreferEndpointSelector("first"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(2));
        Ensure(counter.Count == 2, "initial static topology should dial exactly one connection per endpoint");

        var service = client.Get<IConnectionBehaviorService>();
        Ensure(await service.GetEndpointIdAsync() == "first", "initial selector should route to first endpoint");

        client.UpdateEndpointSelector(new PreferEndpointSelector("second"));
        var calls = new Task<string>[64];
        for (var index = 0; index < calls.Length; index++)
            calls[index] = service.GetEndpointIdAsync().AsTask();
        var routed = await Task.WhenAll(calls);
        Ensure(routed.All(static endpointId => endpointId == "second"),
            "all future concurrent attempts should observe the newly published selector");
        Ensure(counter.Count == 2,
            "runtime selector publication must not reconnect an already Ready static cluster");
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 2,
            "runtime selector publication must not disturb Ready static connections");

        client.UpdateEndpointSelector(new InvalidSelector());
        var invalid = await EnsureThrowsSharpLink(
            service.GetEndpointIdAsync().AsTask(),
            "runtime invalid static selector");
        Ensure(invalid.Code == SharpLinkErrorCode.FailedPrecondition,
            "an invalid selector published at runtime must preserve the existing FailedPrecondition contract");
        Ensure(counter.Count == 2,
            "an invalid runtime selector result must fail only the call and must not reconnect topology");

        client.UpdateEndpointSelector(new PreferEndpointSelector("second"));
        Ensure(await service.GetEndpointIdAsync() == "second",
            "a later valid generation should recover routing after an invalid selector generation");
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
