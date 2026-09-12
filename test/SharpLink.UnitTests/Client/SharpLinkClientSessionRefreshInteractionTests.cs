using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSessionRefreshInteractionTests
{
    [Test]
    [Arguments("fixed", false)]
    [Arguments("fixed", true)]
    [Arguments("static", false)]
    [Arguments("static", true)]
    [Arguments("dynamic", false)]
    [Arguments("dynamic", true)]
    public async Task RefreshReceivedAfterReplacementHandshakeShouldPreserveAdvancedDebt(string pool, bool advance)
    {
        var handshakeCompleted = Signal();
        var releaseReplacement = Signal();
        var attempts = 0;
        async ValueTask BeforeReady(CancellationToken token)
        {
            if (Interlocked.Increment(ref attempts) != 2)
                return;
            handshakeCompleted.TrySetResult();
            await releaseReplacement.Task.WaitAsync(token);
        }
        var factory = new RefreshFactory();
        await using var client = Build(pool, factory, maxConnections: 1, BeforeReady);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        ClientConnection? source = null;
        client._callAdmissionReservedTestHook = connection => source ??= connection;
        var oldCall = ClientInvokerTestHelper.InvokeUnaryAsync(client, cancellationToken: cancellation.Token).AsTask();
        var oldRequest = await factory.Get(0).WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Timeout);
        client._callAdmissionReservedTestHook = null;
        var firstCut = Signal();
        var secondCut = Signal();
        var workerReleased = Signal();
        var cuts = 0;
        client._afterSessionRefreshEligibilitySwapTestHook = () =>
        {
            if (Interlocked.Increment(ref cuts) == 1)
                firstCut.TrySetResult();
            else
                secondCut.TrySetResult();
        };
        client._beforeSessionRefreshWorkerReleaseTestHook = () => workerReleased.TrySetResult();
        var instance = Guid.NewGuid();
        var received = Signal();
        source!.Session.SessionRefreshRequested += request =>
        {
            if (request.DesiredGeneration == (advance ? 3ul : 1ul))
                received.TrySetResult();
        };
        try
        {
            await InjectRefresh(factory.Get(0), instance, 2);
            await handshakeCompleted.Task.WaitAsync(Timeout);
            // The replacement has already handshaken before the newer desired intent arrives.
            // No catch-up notification is sent on that replacement in this test.
            await InjectRefresh(factory.Get(0), instance, 2);
            await InjectRefresh(factory.Get(0), instance, 1);
            if (advance)
                await InjectRefresh(factory.Get(0), instance, 3);
            await received.Task.WaitAsync(Timeout);
            releaseReplacement.TrySetResult();
            await firstCut.Task.WaitAsync(Timeout);
            if (!advance)
                await workerReleased.Task.WaitAsync(Timeout);
            var replacement = source.SessionRefreshRedirect!.Current!;
            var owner = GetOwner(client, pool);
            lock (GetGate(owner, pool))
            {
                var debt = GetField<Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested>>(
                    owner, "_sessionRefreshDebt");
                Ensure(advance
                        ? debt.TryGetValue(replacement, out var pending) &&
                          pending == new ProtocolV2SessionRefreshRequested(instance, 3)
                        : !debt.ContainsKey(replacement),
                    "only an advanced request transfers to the replacement, preserving its exact generation");
                Ensure(!debt.ContainsKey(source), "completed source no longer owns refresh debt");
            }
            Ensure(factory.Count == 2 && client.ReadyConnectionCount == 1,
                "retirement budget keeps transferred debt pending while original work pins the source");
            await factory.Get(0).InjectInt32ResponseAsync(unchecked((long)oldRequest.RequestId));
            Ensure(await oldCall.WaitAsync(Timeout) == 0, "the original source call survives replacement");
            if (advance)
                await secondCut.Task.WaitAsync(Timeout);
            else
                await workerReleased.Task.WaitAsync(Timeout);
            Ensure(factory.Count == (advance ? 3 : 2),
                "advanced debt triggers another replacement without a new rollout; duplicate or older debt does not");
            await CompleteUnary(client, factory.Get(advance ? 2 : 1));
        }
        finally
        {
            releaseReplacement.TrySetResult();
            client._afterSessionRefreshEligibilitySwapTestHook = null;
            client._beforeSessionRefreshWorkerReleaseTestHook = null;
            client._callAdmissionReservedTestHook = null;
            cancellation.Cancel();
            try { await oldCall.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments("fixed")]
    [Arguments("static")]
    [Arguments("dynamic")]
    public async Task PoolShrinkShouldKeepRefreshReplacementSelectableWhileSourceDrains(string pool)
    {
        var factory = new RefreshFactory();
        await using var client = Build(pool, factory, maxConnections: 2);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        ClientConnection? source = null;
        client._callAdmissionReservedTestHook = connection => source ??= connection;
        var oldCall = ClientInvokerTestHelper.InvokeUnaryAsync(client, cancellationToken: cancellation.Token).AsTask();
        var oldRequest = await factory.Get(0).WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Timeout);
        client._callAdmissionReservedTestHook = null;
        var cut = Signal();
        client._afterSessionRefreshEligibilitySwapTestHook = () => cut.TrySetResult();
        try
        {
            await InjectRefresh(factory.Get(0), Guid.NewGuid(), 2);
            await cut.Task.WaitAsync(Timeout);
            var replacement = source!.SessionRefreshRedirect!.Current!;
            Ensure(source.HasPlannedSessionRefreshRetirement && source.ActiveCallCount == 1,
                "the old source is physically Ready only to finish its pre-cut call");
            if (pool == "fixed")
                client.UpdateFixedConnectionPoolSizing(1, 1);
            else
                client.UpdateClusterConnectionPoolSizing(1, 1);
            Ensure(replacement.CanAcceptCalls && client.ReadyConnectionCount == 1 && factory.Count == 2,
                "resizing must retain the one eligible replacement without a zero-ready gap or reconnect");
            await GetField<Task>(client, "_connectionPoolSizingReconcileTask").WaitAsync(Timeout);
            Ensure(source.State == ClientConnectionState.Ready && source.Session.IsConnected && !oldCall.IsCompleted,
                "resizing cannot turn planned source retirement into an early physical drain");
            await CompleteUnary(client, factory.Get(1));
            await factory.Get(0).InjectInt32ResponseAsync(unchecked((long)oldRequest.RequestId));
            Ensure(await oldCall.WaitAsync(Timeout) == 0 && replacement.CanAcceptCalls,
                "old work drains normally while the replacement continues serving calls");
        }
        finally
        {
            client._afterSessionRefreshEligibilitySwapTestHook = null;
            client._callAdmissionReservedTestHook = null;
            cancellation.Cancel();
            try { await oldCall.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task OneWayReservedBeforeFatalPublicationShouldFailWithoutSendingRequest()
    {
        var factory = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(factory);
        await client.ConnectAsync();
        ClientConnection? selected = null;
        client._callAdmissionReservedTestHook = connection =>
        {
            selected = connection;
            connection.ObserveFatalFailureForAdmission();
        };
        try
        {
            SharpLinkException? failure = null;
            try { await ClientInvokerTestHelper.InvokeOneWayAsync(client).AsTask().WaitAsync(Timeout); }
            catch (SharpLinkException exception) { failure = exception; }
            Ensure(failure?.Code == SharpLinkErrorCode.Unavailable,
                "a reserved one-way call must reject a fatal observation before topology cleanup");
            Ensure(selected is { HasObservedFatalFailureForAdmission: true, State: ClientConnectionState.Ready } &&
                   selected.Session.IsConnected,
                "the regression holds the physical session Ready/connected to isolate admission-fatal handling");
            Ensure(selected!.CallAdmissionReservationCount == 0 && selected.ActiveCallCount == 0,
                "failed one-way start releases both the reservation and untracked ownership");
            await selected.Session.FlushSendQueueAsync();
            Ensure(!await factory.Connection.TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)),
                "the rejected one-way invocation never enqueues a Request");
        }
        finally
        {
            client._callAdmissionReservedTestHook = null;
        }
    }

    [Test]
    [Arguments("fixed")]
    [Arguments("static")]
    [Arguments("dynamic")]
    public async Task OrdinaryRegistrationShouldProceedWhileTopologyGateIsHeld(string pool)
    {
        var factory = new RefreshFactory();
        await using var client = Build(pool, factory, maxConnections: 1);
        await client.ConnectAsync();
        var gate = GetGate(GetOwner(client, pool), pool);
        var gateHeld = Signal();
        using var releaseGate = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                gateHeld.TrySetResult();
                Ensure(releaseGate.Wait(Timeout * 2), "test releases the topology gate");
            }
        });
        await gateHeld.Task.WaitAsync(Timeout);
        var call = Task.Run(async () =>
            await ClientInvokerTestHelper.InvokeUnaryAsync(client, cancellationToken: cancellation.Token));
        try
        {
            var request = await factory.Get(0).WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Timeout);
            await factory.Get(0).InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await call.WaitAsync(Timeout) == 0,
                "ordinary registration and completion do not need the unrelated topology gate");
        }
        finally
        {
            releaseGate.Set();
            await holder.WaitAsync(Timeout);
            cancellation.Cancel();
            try { await call.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments("fixed")]
    [Arguments("static")]
    [Arguments("dynamic")]
    public async Task DisconnectedCleanupShouldReleaseTopologyGateAndRemainSupervised(string pool)
    {
        var factory = new RefreshFactory();
        await using var client = Build(pool, factory, maxConnections: 1);
        await client.ConnectAsync();
        ClientConnection? source = null;
        client._callAdmissionReservedTestHook = connection => source = connection;
        var call = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        await factory.Get(0).WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Timeout);
        client._callAdmissionReservedTestHook = null;
        var gate = GetGate(GetOwner(client, pool), pool);
        var cleanupEntered = Signal();
        using var releaseCleanup = new ManualResetEventSlim();
        var cleanupHeldGate = false;
        using var callback = source!.CancellationToken.Register(() =>
        {
            cleanupHeldGate = gate.IsHeldByCurrentThread;
            cleanupEntered.TrySetResult();
            Ensure(releaseCleanup.Wait(Timeout * 2), "test releases disconnected cleanup");
        });
        var failure = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "controlled disconnect");
        var disconnected = Task.Run(() => client.HandleConnectionFatalFailure(source!, failure));
        try
        {
            await cleanupEntered.Task.WaitAsync(Timeout);
            Ensure(!cleanupHeldGate,
                "pending-call failure and synchronous disposal must run outside the topology gate");
            await disconnected.WaitAsync(Timeout);
            Ensure(client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(operation =>
                    operation.Operation.EndsWith("DisconnectedConnectionCleanup", StringComparison.Ordinal)),
                "detached cleanup is supervised before the disconnection handler returns");
            Ensure(!call.IsCompleted, "the pending call remains owned while cleanup is paused");
        }
        finally
        {
            releaseCleanup.Set();
            await disconnected.WaitAsync(Timeout);
        }
        SharpLinkException? observed = null;
        try { await call.WaitAsync(Timeout); }
        catch (SharpLinkException exception) { observed = exception; }
        Ensure(ReferenceEquals(observed, failure), "cleanup preserves the originating connection failure");
        Ensure(source!.CallAdmissionReservationCount == 0 && source.ActiveCallCount == 0,
            "detached cleanup releases all call ownership");
    }

    [Test]
    [Arguments("fixed")]
    [Arguments("static")]
    [Arguments("dynamic")]
    public async Task StopShouldFailConnectionsOutsideTopologyGate(string pool)
    {
        var factory = new RefreshFactory();
        await using var client = Build(pool, factory, maxConnections: 1);
        await client.ConnectAsync();
        ClientConnection? source = null;
        client._callAdmissionReservedTestHook = connection => source = connection;
        await CompleteUnary(client, factory.Get(0));
        client._callAdmissionReservedTestHook = null;
        var gate = GetGate(GetOwner(client, pool), pool);
        var teardownObserved = 0;
        var heldGate = 0;
        client._beforeFatalFailurePublicationTestHook = connection =>
        {
            if (!ReferenceEquals(connection, source))
                return;
            Interlocked.Exchange(ref teardownObserved, 1);
            if (gate.IsHeldByCurrentThread)
                Interlocked.Exchange(ref heldGate, 1);
        };
        try
        {
            await client.StopAsync().AsTask().WaitAsync(Timeout);
            Ensure(Volatile.Read(ref teardownObserved) == 1 && Volatile.Read(ref heldGate) == 0,
                "stop must fail and dispose connections without retaining its caller's topology lock");
            Ensure(source!.State == ClientConnectionState.Closed &&
                   client.FrameworkTaskSnapshotForDiagnostics.IsDrained,
                "stop returns only after connection teardown and supervised cleanup complete");
        }
        finally
        {
            client._beforeFatalFailurePublicationTestHook = null;
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T GetField<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object GetOwner(SharpLinkClient client, string pool)
        => pool == "fixed" ? client : GetField<object>(client, "_cluster");
    private static Lock GetGate(object owner, string pool)
        => GetField<Lock>(owner, pool == "fixed" ? "_poolGate" : "_gate");

    private static SharpLinkClient Build(string pool, RefreshFactory factory, int maxConnections,
        Func<CancellationToken, ValueTask>? beforeReady = null)
    {
        var endpoint = Endpoint("interaction-active", 7201);
        Action<SharpLinkClusterOptions> configure = options =>
        {
            options.MinReadyEndpoints = 1;
            options.MaxConnections = maxConnections;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 1;
        };
        void Configure(SharpClientBuilder builder)
        {
            if (beforeReady is not null)
                builder.UseBeforeReadyPublicationTestHook(beforeReady);
            if (pool == "fixed")
                builder.UseConnectionPool(options =>
                {
                    options.MinConnections = 1;
                    options.MaxConnections = maxConnections;
                });
            else
                builder.UseCluster(configure);
        }
        return pool switch
        {
            "static" => ClientBuilderTestHelper.BuildStatic(
                [new StaticEndpointConfiguration(endpoint, factory),
                 new StaticEndpointConfiguration(Endpoint("interaction-spare", 7202), new RefreshFactory())],
                Configure),
            "dynamic" => ClientBuilderTestHelper.BuildDynamic(new DelegateSharpLinkEndpointResolver(
                    _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, [endpoint])), TimeSpan.FromHours(1)),
                _ => factory, Configure),
            _ => ClientBuilderTestHelper.Build(factory, Configure)
        };
    }

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new() { Id = id, Address = new SharpLinkTcpAddress("127.0.0.1", port) };

    private static async Task CompleteUnary(SharpLinkClient client, TestTransportConnection transport)
    {
        var call = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await transport.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Timeout);
        await transport.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
        Ensure(await call.WaitAsync(Timeout) == 0, "the selected replacement completes a new unary call");
    }

    private static async Task InjectRefresh(TestTransportConnection transport, Guid instance, ulong generation)
    {
        var writer = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(writer, new(instance, generation));
        await transport.InjectFrameAsync(ProtocolV2FrameType.SessionRefreshRequested,
            ProtocolV2FrameFlags.None, 0, writer.WrittenMemory);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RefreshFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        internal int Count { get { lock (_gate) return _connections.Count; } }
        internal TestTransportConnection Get(int index) { lock (_gate) return _connections[index]; }
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = new TestTransportConnection();
            lock (_gate) _connections.Add(connection);
            await connection.InjectSuccessfulHandshakeAsync(ProtocolV2Capabilities.SessionRefresh,
                cancellationToken: cancellationToken);
            return connection;
        }
        public async ValueTask DisposeAsync()
        {
            TestTransportConnection[] connections;
            lock (_gate) connections = [.. _connections];
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }
}
