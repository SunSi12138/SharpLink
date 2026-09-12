using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Guards the boundedness of the session-refresh redirect graph: a long-lived streaming call can
/// pin the oldest generation while many rolling refreshes complete, and stale admission from that
/// pinned source must still resolve to the newest Ready connection instead of walking (or
/// exhausting) a per-generation chain.
/// </summary>
[NotInParallel]
public sealed class SharpLinkClientSessionRefreshRedirectBoundednessTests
{
    private const int RefreshGenerations = 40;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FixedRefreshShouldWaitForPlannedSourceBudgetThenResume(bool latestSourceHasCall)
    {
        var factory = new RepeatedRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var firstCall = ClientInvokerTestHelper.InvokeUnaryAsync(client, cancellationToken: cancellation.Token).AsTask();
        Task<int>? secondCall = null;
        var firstTransport = factory.GetConnection(0);
        var firstRequest = await firstTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var firstCut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client._afterSessionRefreshEligibilitySwapTestHook = () => firstCut.TrySetResult();
        var serverInstanceId = Guid.NewGuid();
        try
        {
            await InjectRefreshAsync(firstTransport, serverInstanceId, 2);
            await firstCut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondTransport = factory.GetConnection(1);
            ProtocolV2FrameHeader secondRequest = default;
            if (latestSourceHasCall)
            {
                secondCall = ClientInvokerTestHelper.InvokeUnaryAsync(client, cancellationToken: cancellation.Token).AsTask();
                secondRequest = await secondTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            var poolGate = (Lock)typeof(SharpLinkClient).GetField("_poolGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            lock (poolGate)
            {
                var canPlan = (bool)typeof(SharpLinkClient).GetMethod(
                    "CanPlanFixedRefreshLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, null)!;
                Ensure(!canPlan,
                    "a physically Ready planned source consumes the fixed retirement budget, even when the next source is idle");
            }

            var nextCut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client._afterSessionRefreshEligibilitySwapTestHook = () => nextCut.TrySetResult();
            var latest = ((ClientConnection[])typeof(SharpLinkClient).GetField(
                "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!)[0];
            latest.Session.SessionRefreshRequested += _ => requestReceived.TrySetResult();
            await InjectRefreshAsync(secondTransport, serverInstanceId, 3);
            await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(factory.ConnectCount == 2 && !nextCut.Task.IsCompleted && client.ReadyConnectionCount == 1,
                "a full retirement budget preserves refresh debt without accumulating a third physical connection");

            await firstTransport.InjectInt32ResponseAsync(unchecked((long)firstRequest.RequestId));
            Ensure(await firstCall.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "the pinned original call drains normally");
            await nextCut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(factory.ConnectCount == 3 && client.ReadyConnectionCount == 1,
                "draining the planned source releases its slot and resumes the same queued refresh request");
            if (secondCall is not null)
            {
                await secondTransport.InjectInt32ResponseAsync(unchecked((long)secondRequest.RequestId));
                Ensure(await secondCall.WaitAsync(TimeSpan.FromSeconds(5)) == 0,
                    "work pinned to the next generation also survives its replacement");
            }
        }
        finally
        {
            client._afterSessionRefreshEligibilitySwapTestHook = null;
            cancellation.Cancel();
            try { await firstCall.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            if (secondCall is not null)
            {
                try { await secondCall.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Test]
    public async Task RepeatedFixedRefreshShouldKeepRedirectBoundedAndReachLatestReady()
    {
        var factory = new RepeatedRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                // One retirement slot stays pinned; the second permits each transient swap.
                options.MaxConnections = 2;
            }));
        await client.ConnectAsync();

        await AssertRepeatedRefreshKeepsRedirectBoundedAsync(client, factory);
    }

    [Test]
    public async Task RepeatedStaticRefreshShouldKeepRedirectBoundedAndReachLatestReady()
    {
        var activeFactory = new RepeatedRefreshTransportFactory();
        var spareFactory = new RepeatedRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(CreateEndpoint("bounded-static", 6901), activeFactory),
            new StaticEndpointConfiguration(CreateEndpoint("bounded-static-spare", 6902), spareFactory)
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 1;
            options.MaxConnections = 1;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 1;
        }));
        await client.ConnectAsync();
        Ensure(activeFactory.ConnectCount == 1,
            "the bounded static setup should initially own the active endpoint");

        await AssertRepeatedRefreshKeepsRedirectBoundedAsync(client, activeFactory);
        Ensure(spareFactory.ConnectCount == 0,
            "repeated same-endpoint refresh must not migrate the lineage to the spare endpoint");
    }

    [Test]
    public async Task RepeatedDynamicRefreshShouldKeepRedirectBoundedAndReachLatestReady()
    {
        var factory = new RepeatedRefreshTransportFactory();
        var endpoint = CreateEndpoint("bounded-dynamic", 7001);
        var resolver = new DelegateSharpLinkEndpointResolver(
            _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, [endpoint])),
            TimeSpan.FromHours(1));
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            _ => factory,
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = 1;
            }));
        await client.ConnectAsync();

        await AssertRepeatedRefreshKeepsRedirectBoundedAsync(client, factory);
    }

    private static async Task AssertRepeatedRefreshKeepsRedirectBoundedAsync(
        SharpLinkClient client,
        RepeatedRefreshTransportFactory factory)
    {
        var serverInstanceId = Guid.NewGuid();
        var pinnedAdmission = new TaskCompletionSource<ClientConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client._callAdmissionReservedTestHook = connection => pinnedAdmission.TrySetResult(connection);

        // Pin the oldest generation with a genuinely long-lived streaming call so planned
        // retirement of that generation can never complete while the refreshes roll forward.
        var streamingCts = new CancellationTokenSource();
        var enumerator = ClientInvokerTestHelper
            .InvokeServerStreaming(client, streamingCts.Token)
            .GetAsyncEnumerator(streamingCts.Token);
        var firstMove = enumerator.MoveNextAsync().AsTask();
        try
        {
            var pinnedSource = await pinnedAdmission.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pinnedTransport = factory.GetConnection(0);
            _ = await pinnedTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(pinnedSource.ActiveCallCount == 1,
                "the pinned generation must own exactly one long-lived streaming call before the refreshes roll forward");

            for (var generation = 2ul; generation <= RefreshGenerations + 1; generation++)
            {
                var currentTransport = factory.GetConnection((int)(generation - 2));
                var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client._afterSessionRefreshEligibilitySwapTestHook = () => cut.TrySetResult();
                await InjectRefreshAsync(currentTransport, serverInstanceId, generation);
                await cut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            client._afterSessionRefreshEligibilitySwapTestHook = null;

            Ensure(factory.ConnectCount == RefreshGenerations + 1,
                $"repeated refresh must not add reconnect dials; connects={factory.ConnectCount}");

            // Admit an ordinary call through the current snapshot and observe which physical
            // connection is selected; that is the newest Ready generation, independently of the
            // redirect structure under test.
            var latestAdmission = new TaskCompletionSource<ClientConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client._callAdmissionReservedTestHook = connection => latestAdmission.TrySetResult(connection);
            var latestTransport = factory.GetConnection(RefreshGenerations);
            var finalUnary = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
            var latest = await latestAdmission.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(!ReferenceEquals(latest, pinnedSource),
                "each successful refresh must publish a fresh Ready connection");
            var finalRequest = await latestTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await latestTransport.InjectInt32ResponseAsync(unchecked((long)finalRequest.RequestId));
            Ensure(await finalUnary.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                "new work after repeated refresh must be served by the newest Ready generation");

            var redirect = pinnedSource.SessionRefreshRedirect
                ?? throw new InvalidOperationException(
                    "the pinned source must hold the shared lineage redirect after its eligibility cut");
            Ensure(ReferenceEquals(redirect.Current, latest),
                "the shared redirect must always target the newest Ready connection");
            Ensure(ReferenceEquals(latest.SessionRefreshRedirect, redirect),
                "every generation of one refresh lineage must share the same redirect indirection");

            var visited = new HashSet<ClientConnection>();
            var cursor = pinnedSource;
            while (cursor is not null && visited.Add(cursor))
                cursor = cursor.SessionRefreshRedirect?.Current;
            Ensure(visited.Count == 2,
                $"the redirect graph must resolve from the pinned source in one hop to the newest Ready connection instead of growing with the generation count; visited={visited.Count} after {RefreshGenerations} refreshes");

            Ensure(pinnedSource.TryReserveCallAdmission(out var admitted),
                "stale admission from the pinned source must still be accepted after many refreshes");
            try
            {
                Ensure(ReferenceEquals(admitted, latest),
                    "stale admission from the pinned source must reach the newest Ready connection, not a disposed predecessor");
            }
            finally
            {
                admitted.ReleaseCallAdmissionReservation();
            }
        }
        finally
        {
            client._afterSessionRefreshEligibilitySwapTestHook = null;
            client._callAdmissionReservedTestHook = null;
            streamingCts.Cancel();
            await enumerator.DisposeAsync().ConfigureAwait(false);
            try
            {
                await firstMove.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            streamingCts.Dispose();
        }
    }

    private static SharpLinkEndpoint CreateEndpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static async Task InjectRefreshAsync(
        TestTransportConnection connection,
        Guid serverInstanceId,
        ulong generation)
    {
        var writer = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(
            writer,
            new ProtocolV2SessionRefreshRequested(serverInstanceId, generation));
        await connection.InjectFrameAsync(
            ProtocolV2FrameType.SessionRefreshRequested,
            ProtocolV2FrameFlags.None,
            0,
            writer.WrittenMemory);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RepeatedRefreshTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = new TestTransportConnection();
            lock (_gate)
            {
                _connections.Add(connection);
                _connectCount++;
            }

            try
            {
                await connection.InjectSuccessfulHandshakeAsync(
                    ProtocolV2Capabilities.SessionRefresh,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal TestTransportConnection GetConnection(int index)
        {
            lock (_gate)
                return _connections[index];
        }

        public async ValueTask DisposeAsync()
        {
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
