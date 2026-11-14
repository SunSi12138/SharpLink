using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSessionRefreshReviewTests
{
    [Test]
    public async Task FixedEligibilityCutMustNotBorrowSourceBindingForReplacement()
    {
        var factory = new ReviewRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();
        var channel = (StaticBindingCallSite)ClientInvokerTestHelper.CreateChannel(client);
        await channel.PrepareAsync(TimeSpan.FromSeconds(3));
        var source = SharpLinkClientLifecycleSharedSupport.GetOnlyReadyConnection(client);
        var sourceToken = channel.Snapshot.Targets[0].Token;
        var cutEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCut = new ManualResetEventSlim(false);
        client._afterSessionRefreshEligibilitySwapTestHook = () =>
        {
            cutEntered.TrySetResult();
            if (!releaseCut.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("fixed refresh cut hook was not released");
        };

        TestTransportConnection? replacement = null;
        try
        {
            await InjectRefreshAsync(factory.GetConnection(0), Guid.NewGuid(), 2);
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            replacement = factory.GetConnection(1);
            replacement.BeforeBindAck = async (_, token) =>
            {
                ackEntered.TrySetResult();
                await releaseAck.Task.WaitAsync(token);
            };
            factory.ReleaseReplacement();
            await cutEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            SharpLinkException? gap = null;
            try { await ClientInvokerTestHelper.InvokeUnaryAsync(client); }
            catch (SharpLinkException exception) { gap = exception; }
            Ensure(gap?.Code == SharpLinkErrorCode.Unavailable &&
                   gap.DetailCode == StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute).DetailCode,
                "a source Session token must not authorize a Ready but unbound refresh replacement");
            Ensure(source.CallAdmissionReservationCount == 0 && source.ActiveCallCount == 0,
                "rejecting a stale bound selection releases its physical admission ownership");
            releaseCut.Set();
            await ackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var prepare = channel.PrepareAsync(TimeSpan.FromSeconds(3)).AsTask();
            Ensure(!prepare.IsCompleted && channel.Snapshot.Targets.Length == 0,
                "the replacement stays explicitly unbound until its own Ack is accepted");
            Ensure(!await replacement.TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)),
                "the rejected call must not send the old token to the new Session");
            releaseAck.TrySetResult();
            await prepare.WaitAsync(TimeSpan.FromSeconds(3));
            var target = channel.Snapshot.Targets[0];
            Ensure(!ReferenceEquals(target.Connection, source) && target.Token != 0 && sourceToken != 0,
                "the receiver's confirmed target belongs to the replacement Session");
            var next = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
            var request = await replacement.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await replacement.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await next.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                "new work succeeds only after the replacement's own binding is published");
        }
        finally
        {
            releaseCut.Set();
            releaseAck.TrySetResult();
            factory.ReleaseReplacement();
            client._afterSessionRefreshEligibilitySwapTestHook = null;
            if (replacement is not null) replacement.BeforeBindAck = null;
        }
    }

    [Test]
    public async Task StaticZeroRetiringBudgetShouldPreserveSelectedCallUntilRegistration()
    {
        var activeFactory = new ReviewRefreshTransportFactory();
        var spareFactory = new ReviewRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(CreateEndpoint("active", 6101), activeFactory),
            new StaticEndpointConfiguration(CreateEndpoint("spare", 6102), spareFactory)
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 1;
            options.MaxConnections = 1;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 0;
        }));
        await client.ConnectAsync();
        Ensure(activeFactory.ConnectCount == 1,
            "the deterministic static setup should initially own the active endpoint");

        await AssertSelectedCallSurvivesZeroRetiringRefreshAsync(client, activeFactory);
    }

    [Test]
    public async Task DynamicZeroRetiringBudgetShouldPreserveSelectedCallUntilRegistration()
    {
        var factory = new ReviewRefreshTransportFactory();
        var endpoint = CreateEndpoint("dynamic", 6201);
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
                options.MaxRetiringConnections = 0;
            }));
        await client.ConnectAsync();

        await AssertSelectedCallSurvivesZeroRetiringRefreshAsync(client, factory);
    }

    [Test]
    public async Task FixedReplacementDisconnectShouldCleanupAndReconnect()
    {
        var factory = new ReviewRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();

        await AssertReplacementDisconnectReconnectsAsync(client, factory);
    }

    [Test]
    public async Task StaticReplacementDisconnectShouldCleanupAndReconnectSameEndpoint()
    {
        var activeFactory = new ReviewRefreshTransportFactory();
        var spareFactory = new ReviewRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(CreateEndpoint("active-disconnect", 6301), activeFactory),
            new StaticEndpointConfiguration(CreateEndpoint("spare-disconnect", 6302), spareFactory)
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
            "the static disconnect setup should initially own the active endpoint");

        await AssertReplacementDisconnectReconnectsAsync(client, activeFactory);
        Ensure(spareFactory.ConnectCount == 0,
            "refresh replacement disconnect should reconnect the owning endpoint instead of migrating topology");
    }

    [Test]
    public async Task DynamicReplacementDisconnectShouldCleanupAndReconnectSameGeneration()
    {
        var factory = new ReviewRefreshTransportFactory();
        var endpoint = CreateEndpoint("dynamic-disconnect", 6401);
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

        await AssertReplacementDisconnectReconnectsAsync(client, factory);
    }

    [Test]
    public async Task FixedWorkerOwnershipReleaseShouldNotStrandLaterRefreshDebt()
    {
        var factory = new ReviewRefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();

        var serverInstanceId = Guid.NewGuid();
        var releaseObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowWorkerExit = new ManualResetEventSlim(false);
        var hookCount = 0;
        client._beforeSessionRefreshWorkerReleaseTestHook = () =>
        {
            if (Interlocked.Increment(ref hookCount) != 1)
                return;
            releaseObserved.TrySetResult();
            if (!allowWorkerExit.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("worker ownership release hook was not released");
        };

        try
        {
            await InjectRefreshAsync(factory.GetConnection(0), serverInstanceId, 2);
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            factory.ReleaseReplacement();
            await releaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var current = factory.GetConnection(1);
            var enqueue = Task.Run(() => InjectRefreshAsync(current, serverInstanceId, 3));
            await Task.Yield();
            allowWorkerExit.Set();
            await enqueue;

            await WaitForConditionAsync(
                () => factory.ConnectCount >= 3,
                "refresh debt arriving at worker ownership hand-off should start a successor instead of becoming stranded");
        }
        finally
        {
            allowWorkerExit.Set();
            client._beforeSessionRefreshWorkerReleaseTestHook = null;
        }
    }

    private static async Task AssertSelectedCallSurvivesZeroRetiringRefreshAsync(
        SharpLinkClient client,
        ReviewRefreshTransportFactory factory)
    {
        await ClientInvokerTestHelper.PrepareAsync(client);
        var sourceTransport = factory.GetConnection(0);
        var selected = new TaskCompletionSource<ClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSelection = new ManualResetEventSlim(false);
        var hookClaimed = 0;
        client._callAdmissionReservedTestHook = connection =>
        {
            if (Interlocked.Exchange(ref hookClaimed, 1) != 0)
                return;
            selected.TrySetResult(connection);
            if (!releaseSelection.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("selected-call admission hook was not released");
        };
        client._afterSessionRefreshEligibilitySwapTestHook = () => cut.TrySetResult();

        try
        {
            var unary = Task.Run(async () => await ClientInvokerTestHelper.InvokeUnaryAsync(client));
            var source = await selected.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await InjectRefreshAsync(sourceTransport, Guid.NewGuid(), 2);
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            factory.ReleaseReplacement();
            await cut.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Ensure(source.State == ClientConnectionState.Ready,
                "MaxRetiringConnections=0 must keep a source with an admitted selection physically Ready until registration/drain ownership transfers");
            Ensure(client.ReadyConnectionCount == 1,
                "the replacement must be the only published Ready connection after the eligibility cut");

            releaseSelection.Set();
            var request = await sourceTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await sourceTransport.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await unary.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                "a call selected before the cut must be formally registered and complete on its reserved source");
        }
        finally
        {
            releaseSelection.Set();
            client._callAdmissionReservedTestHook = null;
            client._afterSessionRefreshEligibilitySwapTestHook = null;
        }
    }

    private static async Task AssertReplacementDisconnectReconnectsAsync(
        SharpLinkClient client,
        ReviewRefreshTransportFactory factory)
    {
        var eligibilityCut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client._afterSessionRefreshEligibilitySwapTestHook = () => eligibilityCut.TrySetResult();
        try
        {
            await InjectRefreshAsync(factory.GetConnection(0), Guid.NewGuid(), 2);
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            factory.ReleaseReplacement();
            // The replacement can send its first Ping before the source loses call eligibility.
            // Observe the actual routing cut before issuing a call intended for the replacement.
            await eligibilityCut.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            factory.ReleaseReplacement();
            client._afterSessionRefreshEligibilitySwapTestHook = null;
        }

        var replacement = factory.GetConnection(1);
        Ensure(client.ReadyConnectionCount == 1,
            "after replacement publication the refreshed connection should be the sole Ready connection");

        await ClientInvokerTestHelper.PrepareAsync(client);
        var pending = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await replacement.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));

        await replacement.DisposeAsync();
        var failure = await CaptureFailureAsync(pending);
        Ensure(failure is not TimeoutException,
            "disconnecting the published replacement should terminate its pending unary instead of leaving it unresolved");

        await WaitForConditionAsync(
            () => factory.ConnectCount >= 3 && client.ReadyConnectionCount == 1,
            "replacement disconnect callback must remove the exact published connection and start reconnect");
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected the pending call to fail after replacement disconnect.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException(message);
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

    private sealed class ReviewRefreshTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly TaskCompletionSource _replacementStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReplacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal Task ReplacementStarted => _replacementStarted.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _connectCount);
            var connection = new TestTransportConnection();
            lock (_gate)
                _connections.Add(connection);

            try
            {
                if (ordinal == 2)
                {
                    _replacementStarted.TrySetResult();
                    await _releaseReplacement.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await connection.InjectSuccessfulHandshakeAsync(
                    ProtocolV2Capabilities.SessionRefresh,
                    contractManifest: ClientInvocationTestManifest.RemoteContracts,
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

        internal void ReleaseReplacement() => _releaseReplacement.TrySetResult();

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
