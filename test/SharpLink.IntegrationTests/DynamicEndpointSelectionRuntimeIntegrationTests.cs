namespace SharpLink.IntegrationTests;

public sealed partial class DynamicEndpointIntegrationTests
{
    [Test]
    [Arguments(100)]
    [Arguments(700)]
    [NotInParallel]
    public async Task RuntimeSelectionPolicyShouldComposeWithDynamicTopologyReplacementWithoutExtraDial(int streamDelayMs)
    {
        var counter = new RuntimeSelectionConnectCounter();
        var capture = new RuntimeSelectionDiagnosticCapture(counter);
        Exception? failure = null;
        var bodyCompleted = false;
        try
        {
            await using var first = await TcpServerScope.StartAsync("first", capture.ForEndpoint("first"));
            await using var second = await TcpServerScope.StartAsync("second", capture.ForEndpoint("second"));
            await using var third = await TcpServerScope.StartAsync("third", capture.ForEndpoint("third"));
            var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
                1,
                [Endpoint("first", first.Port, "blue"), Endpoint("second", second.Port, "green")]));
            var sockets = SharpLinkTransportFactories.Sockets();
            await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
                // Match TcpServerScope's 500 ms idle watchdog. The default 10 s heartbeat
                // would legitimately reconnect idle siblings while the accepted stream drains.
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .UseEndpointResolver(
                    resolver,
                    endpoint => new RuntimeSelectionCountingTransportFactory(sockets(endpoint), counter, endpoint.Id))
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
            counter.EnsureCounts("initial topology", first: 1, second: 1, third: 0);
            capture.InitialCountsVerified = true;

            var service = client.Get<IConnectionBehaviorService>();
            Ensure(await service.GetEndpointIdAsync() == "first", "initial dynamic selector should route to first");

            counter.Phase = "policy-only publication";
            client.UpdateEndpointSelector(new IdSelector("second"));
            Ensure(await service.GetEndpointIdAsync() == "second",
                "a live dynamic client should route its next physical attempt with the new selector");
            counter.EnsureCounts("policy-only publication must not reconnect", first: 1, second: 1, third: 0);
            capture.PolicyOnlyCountsVerified = true;

            // The 700 ms case keeps healthy siblings idle beyond the server watchdog;
            // matching heartbeat producers, rather than a fast test run, must preserve them.
            await using var retiringStream = service
                .SlowRangeAsync(3, streamDelayMs, CancellationToken.None)
                .GetAsyncEnumerator();
            Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 0,
                "the accepted stream should bind to the endpoint selected before retirement begins");

            counter.Phase = "concurrent topology/policy publication";
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

            counter.Phase = "accepted stream drain";
            Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 1,
                "policy/topology publication must not move an accepted stream off its retiring endpoint");
            Ensure(await retiringStream.MoveNextAsync() && retiringStream.Current == 2,
                "the retiring endpoint must remain bound through the accepted stream's final item");
            Ensure(!await retiringStream.MoveNextAsync(),
                "the accepted stream should complete normally while its endpoint drains");

            counter.Phase = "replacement readiness";
            await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2, TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                async () => await service.GetEndpointIdAsync() == "third",
                TimeSpan.FromSeconds(3));
            counter.EnsureCounts(
                "resolver replacement requires exactly one new dial; policy publications must add none",
                first: 1, second: 1, third: 1);
            Ensure(client.GetReadinessSnapshot().ActiveEndpoints == 2,
                "selection publication must not alter resolver-owned endpoint membership");
            bodyCompleted = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            capture.WriteRecord(streamDelayMs, bodyCompleted, failure);
        }
    }

    private sealed class RuntimeSelectionConnectCounter
    {
        private readonly Lock _gate = new();
        private readonly List<(string Endpoint, string Phase)> _attempts = [];
        private string _phase = "initial topology";

        public string Phase
        {
            get { lock (_gate) return _phase; }
            set { lock (_gate) _phase = value; }
        }

        public void Increment(string endpoint)
        {
            lock (_gate)
                _attempts.Add((endpoint, _phase));
        }

        public (string Endpoint, string Phase)[] CaptureAttempts()
        {
            lock (_gate)
                return _attempts.ToArray();
        }

        public void EnsureCounts(string phase, int first, int second, int third)
        {
            (string Endpoint, string Phase)[] attempts;
            lock (_gate)
                attempts = _attempts.ToArray();
            var actualFirst = attempts.Count(attempt => attempt.Endpoint == "first");
            var actualSecond = attempts.Count(attempt => attempt.Endpoint == "second");
            var actualThird = attempts.Count(attempt => attempt.Endpoint == "third");
            Ensure(attempts.Length == first + second + third &&
                actualFirst == first && actualSecond == second && actualThird == third,
                $"{phase}: expected total={first + second + third}, first={first}, second={second}, third={third}; " +
                $"actual total={attempts.Length}, first={actualFirst}, second={actualSecond}, third={actualThird}; " +
                $"dial sources=[{string.Join(", ", attempts.Select(attempt => $"{attempt.Endpoint}@{attempt.Phase}"))}]");
        }
    }

    private sealed class RuntimeSelectionCountingTransportFactory(
        IClientTransportFactory inner,
        RuntimeSelectionConnectCounter counter,
        string endpoint) : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            counter.Increment(endpoint);
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
