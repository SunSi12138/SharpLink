using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class EndpointSelectionRuntimeTests
{
    [Test]
    public async Task BuiltInAndCustomTransitionsShouldPublishCompleteGenerations()
    {
        await using var client = CreateStaticClient();
        var initial = client.GetEndpointSelectionPolicySnapshot();
        EnsureBuiltIn(initial, generation: 0, SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);

        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.Random);
        EnsureBuiltIn(
            client.GetEndpointSelectionPolicySnapshot(),
            generation: 1,
            SharpLinkLoadBalancingStrategy.Random);

        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
        EnsureBuiltIn(
            client.GetEndpointSelectionPolicySnapshot(),
            generation: 2,
            SharpLinkLoadBalancingStrategy.RoundRobin);

        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending);
        EnsureBuiltIn(
            client.GetEndpointSelectionPolicySnapshot(),
            generation: 3,
            SharpLinkLoadBalancingStrategy.LeastPending);

        var firstSelector = new FixedIndexSelector(0);
        client.UpdateEndpointSelector(firstSelector);
        EnsureCustom(client.GetEndpointSelectionPolicySnapshot(), generation: 4);

        client.UpdateEndpointSelector(firstSelector);
        EnsureCustom(client.GetEndpointSelectionPolicySnapshot(), generation: 4);

        client.UpdateEndpointSelector(new FixedIndexSelector(1));
        EnsureCustom(client.GetEndpointSelectionPolicySnapshot(), generation: 5);

        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
        EnsureBuiltIn(
            client.GetEndpointSelectionPolicySnapshot(),
            generation: 6,
            SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
    }

    [Test]
    public async Task DynamicPolicyUpdatesBeforeConnectShouldNotDialOrMutateTopology()
    {
        var counter = new ConnectCounter();
        var resolver = new FixedResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic-a", 6201), Endpoint("dynamic-b", 6202)]));
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpointResolver(resolver, _ => new CountingTransportFactory(counter))
            .Build();

        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.Random);
        client.UpdateEndpointSelector(new FixedIndexSelector(0));
        client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending);

        for (var index = 0; index < 8; index++)
            await Task.Yield();

        Ensure(counter.Count == 0,
            "publishing endpoint-selection policy before ConnectAsync must not start transport work");
        EnsureBuiltIn(
            client.GetEndpointSelectionPolicySnapshot(),
            generation: 3,
            SharpLinkLoadBalancingStrategy.LeastPending);
    }

    [Test]
    public async Task FixedAndSingleEndpointFastPathsShouldRejectRuntimeSelectionPolicy()
    {
        await using var fixedClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTransport(new NeverUsedTransportFactory())
            .Build();
        await using var singleStaticClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpoint(Endpoint("single", 6251), _ => new NeverUsedTransportFactory())
            .Build();

        foreach (var client in new[] { fixedClient, singleStaticClient })
        {
            EnsureThrows<NotSupportedException>(() => client.GetEndpointSelectionPolicySnapshot());
            EnsureThrows<NotSupportedException>(() =>
                client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.Random));
            EnsureThrows<NotSupportedException>(() =>
                client.UpdateEndpointSelector(new FixedIndexSelector(0)));
        }
    }

    [Test]
    public async Task InvalidBuiltInCandidateShouldRollbackWithoutPublishing()
    {
        await using var client = CreateStaticClient();
        var before = client.GetEndpointSelectionPolicySnapshot();

        EnsureThrows<ArgumentOutOfRangeException>(() =>
            client.UpdateLoadBalancing((SharpLinkLoadBalancingStrategy)int.MaxValue));

        Ensure(client.GetEndpointSelectionPolicySnapshot() == before,
            "invalid built-in strategy must not advance or partially replace the published generation");
    }

    [Test]
    public async Task StopShouldSealFurtherSelectionPolicyPublication()
    {
        await using var client = CreateStaticClient();
        await client.StopAsync();

        EnsureThrows<InvalidOperationException>(() =>
            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.Random));
        EnsureThrows<InvalidOperationException>(() =>
            client.UpdateEndpointSelector(new FixedIndexSelector(0)));
    }

    [Test]
    public async Task ConcurrentPublicationAndObservationShouldNeverExposeTornPolicy()
    {
        await using var client = CreateStaticClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var custom = new FixedIndexSelector(0);

        var writers = new Task[4];
        for (var writer = 0; writer < writers.Length; writer++)
        {
            var writerIndex = writer;
            writers[writer] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var iteration = 0; iteration < 1_000; iteration++)
                {
                    switch ((iteration + writerIndex) % 5)
                    {
                        case 0:
                            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
                            break;
                        case 1:
                            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.Random);
                            break;
                        case 2:
                            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
                            break;
                        case 3:
                            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending);
                            break;
                        default:
                            client.UpdateEndpointSelector(custom);
                            break;
                    }
                }
            });
        }

        var readers = new Task[4];
        for (var reader = 0; reader < readers.Length; reader++)
        {
            readers[reader] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                ulong previousGeneration = 0;
                for (var iteration = 0; iteration < 20_000; iteration++)
                {
                    var snapshot = client.GetEndpointSelectionPolicySnapshot();
                    Ensure(snapshot.Generation >= previousGeneration,
                        "one reader must never observe endpoint-selection generations moving backwards");
                    previousGeneration = snapshot.Generation;
                    if (snapshot.Kind == SharpLinkEndpointSelectionPolicyKind.BuiltIn)
                    {
                        Ensure(snapshot.BuiltInStrategy.HasValue && Enum.IsDefined(snapshot.BuiltInStrategy.Value),
                            "built-in publication must expose one complete valid strategy");
                    }
                    else
                    {
                        Ensure(snapshot.Kind == SharpLinkEndpointSelectionPolicyKind.Custom &&
                               snapshot.BuiltInStrategy is null,
                            "custom publication must never expose a torn built-in strategy field");
                    }
                }
            });
        }

        start.TrySetResult();
        await Task.WhenAll(writers);
        await Task.WhenAll(readers);
    }

    private static ISharpLinkClient CreateStaticClient()
        => SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpoints(
                [Endpoint("static-a", 6101), Endpoint("static-b", 6102)],
                _ => new NeverUsedTransportFactory())
            .Build();

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private static void EnsureBuiltIn(
        SharpLinkEndpointSelectionPolicySnapshot snapshot,
        ulong generation,
        SharpLinkLoadBalancingStrategy strategy)
    {
        Ensure(snapshot.Generation == generation, "unexpected endpoint-selection generation");
        Ensure(snapshot.Kind == SharpLinkEndpointSelectionPolicyKind.BuiltIn,
            "expected built-in endpoint-selection policy");
        Ensure(snapshot.BuiltInStrategy == strategy, "unexpected built-in endpoint-selection strategy");
    }

    private static void EnsureCustom(SharpLinkEndpointSelectionPolicySnapshot snapshot, ulong generation)
    {
        Ensure(snapshot.Generation == generation, "unexpected endpoint-selection generation");
        Ensure(snapshot.Kind == SharpLinkEndpointSelectionPolicyKind.Custom,
            "expected custom endpoint-selection policy");
        Ensure(snapshot.BuiltInStrategy is null,
            "custom endpoint-selection snapshot must not expose a built-in strategy");
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"expected {typeof(TException).Name}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FixedIndexSelector(int index) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => index;
    }

    private sealed class ConnectCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingTransportFactory(ConnectCounter counter) : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return ValueTask.FromException<ITransportConnection>(new IOException("unexpected selection-policy dial"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverUsedTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("transport must not be used by selection-policy test"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
