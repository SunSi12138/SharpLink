using System.Linq;
using System.Reflection;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientRuntimeConnectionPoolSizingTests
{
    [Test]
    public async Task FixedPoolMinimumIncreaseShouldConvergeWithoutRebuildingClient()
    {
        var transport = new SequenceClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 3;
            }));
        try
        {
            await client.ConnectAsync();
            Ensure(client.ReadyConnectionCount == 1, "fixed pool initial ready count");

            ((ISharpLinkClient)client).UpdateFixedConnectionPoolSizing(3, 3);
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 3,
                () => $"fixed pool did not grow to three connections; ready={client.ReadyConnectionCount}, connects={transport.ConnectCount}");

            var snapshot = ((ISharpLinkClient)client).GetConnectionPoolSizingSnapshot();
            Ensure(snapshot.Generation == 1 &&
                   snapshot.Kind == SharpLinkConnectionPoolSizingKind.FixedEndpoint &&
                   snapshot.MinConnections == 3 && snapshot.MaxConnections == 3,
                "fixed pool growth snapshot");
            Ensure(transport.ConnectCount == 3, "fixed pool must reuse the existing connection lifecycle for growth");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task FixedPoolMaximumShrinkShouldDrainActiveConnectionBeforeClosingIt()
    {
        var transport = new SequenceClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            }));
        ClientConnection? first = null;
        ClientConnection? second = null;
        try
        {
            await client.ConnectAsync();
            var ready = GetFixedReadyConnections(client);
            Ensure(ready.Length == 2, "fixed shrink setup requires two ready connections");
            first = ready[0];
            second = ready[1];
            Ensure(first.TryBeginUntrackedCall(), "first active-call setup");
            Ensure(second.TryBeginUntrackedCall(), "second active-call setup");

            ((ISharpLinkClient)client).UpdateFixedConnectionPoolSizing(1, 1);
            await WaitUntilAsync(
                () => first.State == ClientConnectionState.Draining || second.State == ClientConnectionState.Draining,
                () => $"no active connection entered Draining; first={first.State}, second={second.State}");

            var draining = first.State == ClientConnectionState.Draining ? first : second;
            var retained = ReferenceEquals(draining, first) ? second : first;
            Ensure(draining.ActiveCallCount == 1 && draining.Session.IsConnected,
                "scale-down must keep the active draining connection alive");
            Ensure(client.ReadyConnectionCount == 1 && retained.State == ClientConnectionState.Ready,
                "the draining connection must stop accepting new calls immediately");

            draining.EndUntrackedCall();
            if (ReferenceEquals(draining, first))
                first = null;
            else
                second = null;
            await WaitUntilAsync(
                () => draining.State == ClientConnectionState.Closed,
                () => $"draining connection did not retire after its active call completed; state={draining.State}");
        }
        finally
        {
            if (first is not null && first.ActiveCallCount != 0)
                first.EndUntrackedCall();
            if (second is not null && second.ActiveCallCount != 0)
                second.EndUntrackedCall();
            await client.StopAsync();
        }
    }

    [Test]
    public async Task StaticClusterShrinkShouldNotForceCloseActiveSurplusWhenRetiringBudgetIsZero()
    {
        var first = new SequenceClientTransportFactory();
        var second = new SequenceClientTransportFactory();
        var client = ClientBuilderTestHelper.BuildStatic(
            [
                new StaticEndpointConfiguration(CreateEndpoint("static-a", 5201), first),
                new StaticEndpointConfiguration(CreateEndpoint("static-b", 5202), second)
            ],
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 2;
                options.MaxRetiringConnections = 0;
            }));
        ClientConnection[] ready = [];
        try
        {
            await client.ConnectAsync();
            ready = await ExpandStaticClusterToThreeReadyConnectionsAsync(client);
            Ensure(ready.Length == 3, "static resize setup requires three ready connections");
            for (var index = 0; index < ready.Length; index++)
                Ensure(ready[index].TryBeginUntrackedCall(), $"static active-call setup {index}");

            ((ISharpLinkClient)client).UpdateClusterConnectionPoolSizing(2, 1);
            await Task.Delay(100);

            Ensure(ready.All(static connection => connection.State == ClientConnectionState.Ready),
                "resize must not route active surplus through force-close when retiring budget is zero");
            Ensure(ready.All(static connection => connection.Session.IsConnected),
                "active surplus sessions must stay connected until their work drains");
            Ensure(client.ReadyConnectionCount == 3,
                "the smaller limit may wait for active work instead of cancelling it");

            for (var index = 0; index < ready.Length; index++)
                ready[index].EndUntrackedCall();
            ready = [];

            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 2,
                () => $"static cluster did not converge after active work drained; ready={client.ReadyConnectionCount}");
            var snapshot = ((ISharpLinkClient)client).GetConnectionPoolSizingSnapshot();
            Ensure(snapshot.MaxConnections == 2 && snapshot.MaxConnectionsPerEndpoint == 1,
                "static cluster converged sizing snapshot");
        }
        finally
        {
            for (var index = 0; index < ready.Length; index++)
            {
                if (ready[index].ActiveCallCount != 0)
                    ready[index].EndUntrackedCall();
            }
            await client.StopAsync();
        }
    }

    [Test]
    public async Task StaticResizeReservationShouldBeVisibleToNormalRetirementBudget()
    {
        var first = new SequenceClientTransportFactory();
        var second = new SequenceClientTransportFactory();
        var client = ClientBuilderTestHelper.BuildStatic(
            [
                new StaticEndpointConfiguration(CreateEndpoint("shared-budget-a", 5301), first),
                new StaticEndpointConfiguration(CreateEndpoint("shared-budget-b", 5302), second)
            ],
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 3;
                options.MaxConnectionsPerEndpoint = 2;
                options.MaxRetiringConnections = 1;
            }));
        ClientConnection[] ready = [];
        try
        {
            await client.ConnectAsync();
            ready = await ExpandStaticClusterToThreeReadyConnectionsAsync(client);
            Ensure(ready.Length == 3, "shared-budget setup requires three ready connections");
            for (var index = 0; index < ready.Length; index++)
                Ensure(ready[index].TryBeginUntrackedCall(), $"shared-budget active-call setup {index}");

            ((ISharpLinkClient)client).UpdateClusterConnectionPoolSizing(2, 2);
            await WaitUntilAsync(
                () => ready.Count(static connection => connection.State == ClientConnectionState.Draining) == 1,
                () => "resize did not reserve exactly one active retirement slot");

            var resizeDraining = ready.Single(static connection => connection.State == ClientConnectionState.Draining);
            Ensure(resizeDraining.Session.IsConnected,
                "resize-owned active drain must remain connected while its work is active");
            var normalCandidate = ready.First(static connection => connection.State == ClientConnectionState.Ready);

            var cluster = GetEndpointClusterRuntime(client);
            var markDraining = cluster.GetType().GetMethod(
                "MarkConnectionDraining",
                BindingFlags.Instance | BindingFlags.Public)
                ?? throw new Exception("cannot find static cluster normal retirement entry point");
            markDraining.Invoke(cluster, [normalCandidate]);

            await WaitUntilAsync(
                () => normalCandidate.State == ClientConnectionState.Closed,
                () => $"normal retirement did not observe the resize reservation; state={normalCandidate.State}");
            Ensure(resizeDraining.State == ClientConnectionState.Draining && resizeDraining.Session.IsConnected,
                "normal retirement overflow must not evict the resize-owned active drain");
            Ensure(ready.Count(static connection => connection.State == ClientConnectionState.Draining) == 1,
                "resize and normal retirement must share one effective active-retirement budget");
        }
        finally
        {
            for (var index = 0; index < ready.Length; index++)
            {
                if (ready[index].ActiveCallCount != 0)
                    ready[index].EndUntrackedCall();
            }
            await client.StopAsync();
        }
    }

    [Test]
    public async Task InvalidFixedSizingAndStopShouldLeavePublishedGenerationStable()
    {
        var client = ClientBuilderTestHelper.Build(new SequenceClientTransportFactory());
        var runtime = (ISharpLinkClient)client;
        try
        {
            var initial = runtime.GetConnectionPoolSizingSnapshot();
            Ensure(initial.Generation == 0, "initial sizing generation");

            EnsureThrows<ArgumentException>(() => runtime.UpdateFixedConnectionPoolSizing(2, 1));
            var afterInvalid = runtime.GetConnectionPoolSizingSnapshot();
            Ensure(afterInvalid == initial, "invalid sizing candidate must not publish");

            await client.StopAsync();
            EnsureThrows<InvalidOperationException>(() => runtime.UpdateFixedConnectionPoolSizing(1, 2));
            Ensure(runtime.GetConnectionPoolSizingSnapshot() == initial,
                "Stop must seal sizing publication");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task StaticAndDynamicTopologiesShouldUseClusterSizingModelOnly()
    {
        var first = new SequenceClientTransportFactory();
        var second = new SequenceClientTransportFactory();
        var staticClient = ClientBuilderTestHelper.BuildStatic(
            [
                new StaticEndpointConfiguration(CreateEndpoint("static-a", 5101), first),
                new StaticEndpointConfiguration(CreateEndpoint("static-b", 5102), second)
            ],
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 2;
            }));
        try
        {
            var runtime = (ISharpLinkClient)staticClient;
            runtime.UpdateClusterConnectionPoolSizing(3, 1);
            var snapshot = runtime.GetConnectionPoolSizingSnapshot();
            Ensure(snapshot.Generation == 1 &&
                   snapshot.Kind == SharpLinkConnectionPoolSizingKind.EndpointCluster &&
                   snapshot.MinConnections == 0 &&
                   snapshot.MaxConnections == 3 &&
                   snapshot.MaxConnectionsPerEndpoint == 1,
                "static cluster sizing snapshot");
            EnsureThrows<InvalidOperationException>(() => runtime.UpdateFixedConnectionPoolSizing(1, 2));
        }
        finally
        {
            await staticClient.StopAsync();
        }

        var dynamicClient = ClientBuilderTestHelper.BuildDynamic(
            new EmptyResolver(),
            static _ => new NonConnectingFactory(),
            builder => builder.UseCluster(options =>
            {
                options.MaxEndpoints = 4;
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 4;
                options.MaxConnectionsPerEndpoint = 2;
            }));
        try
        {
            var runtime = (ISharpLinkClient)dynamicClient;
            runtime.UpdateClusterConnectionPoolSizing(2, 1);
            var snapshot = runtime.GetConnectionPoolSizingSnapshot();
            Ensure(snapshot.Generation == 1 &&
                   snapshot.Kind == SharpLinkConnectionPoolSizingKind.EndpointCluster &&
                   snapshot.MaxConnections == 2 &&
                   snapshot.MaxConnectionsPerEndpoint == 1,
                "dynamic cluster sizing snapshot");
            EnsureThrows<InvalidOperationException>(() => runtime.UpdateFixedConnectionPoolSizing(1, 2));
        }
        finally
        {
            await dynamicClient.StopAsync();
        }
    }

    private static ClientConnection[] GetFixedReadyConnections(SharpLinkClient client)
    {
        var field = typeof(SharpLinkClient).GetField(
            "_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find fixed ready connection publication");
        return (ClientConnection[])field.GetValue(client)!;
    }

    private static async Task<ClientConnection[]> ExpandStaticClusterToThreeReadyConnectionsAsync(SharpLinkClient client)
    {
        var cluster = GetEndpointClusterRuntime(client);
        var endpointsField = cluster.GetType().GetField(
            "_endpoints",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find static cluster endpoint states");
        var endpoints = (StaticClientRuntimeEndpointState[])endpointsField.GetValue(cluster)!;
        var ensureExpansion = cluster.GetType().GetMethod(
            "EnsureExpansion",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find static cluster expansion coordinator");
        ensureExpansion.Invoke(cluster, [endpoints[0]]);

        await WaitUntilAsync(
            () => client.ReadyConnectionCount == 3,
            () => $"static cluster did not expand to three ready connections; ready={client.ReadyConnectionCount}");
        var captureReady = cluster.GetType().GetMethod(
            "CaptureReadyConnections",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot capture static cluster ready connections");
        return (ClientConnection[])captureReady.Invoke(cluster, null)!;
    }

    private static object GetEndpointClusterRuntime(SharpLinkClient client)
    {
        var clusterField = typeof(SharpLinkClient).GetField(
            "_cluster",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find endpoint cluster runtime");
        return clusterField.GetValue(client)
            ?? throw new Exception("Client did not materialize an endpoint cluster runtime");
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private sealed class EmptyResolver : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, []));

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
