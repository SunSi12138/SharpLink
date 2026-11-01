using SharpLink.Client;
using System.Reflection;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSessionRefreshReviewTests
{
    [Test]
    public async Task FixedEligibilityCutShouldKeepContractIdentityWhileLowerAdmissionRedirects()
    {
        var factory = new ReviewRefreshTransportFactory();
        var client = ClientBuilderTestHelper.Build(factory, builder => builder.UseConnectionPool(options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 1;
        }));
        await WithOwnedClientAsync(client, async () =>
        {
            await client.ConnectAsync();
            var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
            var source = SharpLinkClientLifecycleSharedSupport.GetOnlyReadyConnection(client);
            var cutEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCut = new ManualResetEventSlim(false);
            client._afterSessionRefreshEligibilitySwapTestHook = () =>
            {
                cutEntered.TrySetResult();
                if (!releaseCut.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("fixed refresh cut hook was not released");
            };
            Task? cutInvocation = null;
            Exception? cutPrimary = null;
            try
            {
                await InjectRefreshAsync(factory.GetConnection(0), factory.GetConnection(0).Authority.ServerInstanceId, 2);
                await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
                factory.ReleaseReplacement();
                await cutEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Ensure(source.TryReserveCallAdmission(out var redirected), "lower-level admission must follow the refresh redirect");
                try { Ensure(!ReferenceEquals(redirected, source), "the reservation must belong to the replacement physical owner"); }
                finally { redirected.ReleaseCallAdmissionReservation(); }

                // A physical redirect cannot carry the old contract token. Publication is deliberately
                // held here: fail fast, never wait for Prepare behind the barrier we ourselves own.
                cutInvocation = Task.Run(async () => await InvokeUnary(channel));
                await RequireNoBoundRouteAsync(cutInvocation);
                Ensure(!await factory.GetConnection(0).TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)) &&
                       !await factory.GetConnection(1).TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)),
                    "neither physical owner may receive a request under an unbound replacement token");
            }
            catch (Exception error) { cutPrimary = error; }
            finally
            {
                releaseCut.Set();
                factory.ReleaseReplacement();
                client._afterSessionRefreshEligibilitySwapTestHook = null;
            }
            await FinishOwnedInvocationAsync(client, cutInvocation, cutPrimary, expectNoBoundRoute: true);
            await channel.PrepareAsync(TimeSpan.FromSeconds(5));
            Task<int>? next = null;
            Exception? primary = null;
            try
            {
                next = InvokeUnary(channel).AsTask();
                var replacement = factory.GetConnection(1);
                var request = await replacement.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(2));
                await replacement.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
                Ensure(await next.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                    "the same identity-bearing channel succeeds only after a new binding publishes");
            }
            catch (Exception error) { primary = error; }
            await FinishOwnedInvocationAsync(client, next, primary);
        });
    }

    [Test]
    public async Task FixedSelectedCallShouldPreserveOriginalOwnerUntilRegistration()
    {
        var factory = new ReviewRefreshTransportFactory();
        var client = ClientBuilderTestHelper.Build(factory, builder => builder.UseConnectionPool(options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 1;
        }));
        await WithOwnedClientAsync(client, async () =>
        {
            await client.ConnectAsync();
            await AssertSelectedCallSurvivesZeroRetiringRefreshAsync(client, factory);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task RetiredTokenAfterReservationMustRejectBeforePendingOrRequest(bool clustered)
        => AssertPostReservationRejectionAsync(clustered, invalidateToken: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FatalOwnerAfterReservationMustRejectBeforePendingOrRequest(bool clustered)
        => AssertPostReservationRejectionAsync(clustered, invalidateToken: false);

    [Test]
    public async Task StaticZeroRetiringBudgetShouldPreserveSelectedCallUntilRegistration()
    {
        var activeFactory = new ReviewRefreshTransportFactory();
        var spareFactory = new ReviewRefreshTransportFactory();
        var client = ClientBuilderTestHelper.BuildStatic(
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
        await WithOwnedClientAsync(client, async () =>
        {
            await client.ConnectAsync();
            Ensure(activeFactory.ConnectCount == 1,
                "the deterministic static setup should initially own the active endpoint");

            await AssertSelectedCallSurvivesZeroRetiringRefreshAsync(client, activeFactory);
        });
    }

    [Test]
    public async Task DynamicZeroRetiringBudgetShouldPreserveSelectedCallUntilRegistration()
    {
        var factory = new ReviewRefreshTransportFactory();
        var endpoint = CreateEndpoint("dynamic", 6201);
        var resolver = new DelegateSharpLinkEndpointResolver(
            _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, [endpoint])),
            TimeSpan.FromHours(1));
        var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            _ => factory,
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = 0;
            }));
        await WithOwnedClientAsync(client, async () =>
        {
            await client.ConnectAsync();

            await AssertSelectedCallSurvivesZeroRetiringRefreshAsync(client, factory);
        });
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

        var serverInstanceId = factory.GetConnection(0).Authority.ServerInstanceId;
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
            var enqueue = Task.Run(() => InjectRefreshAsync(current, current.Authority.ServerInstanceId, 3));
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

    private static async Task WithOwnedClientAsync(SharpLinkClient client, Func<Task> body)
    {
        var failures = new List<Exception>();
        try { await body(); } catch (Exception error) { failures.Add(error); }
        try { await client.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        ThrowFailures(failures);
    }

    private static async Task FinishOwnedInvocationAsync(SharpLinkClient client, Task? invocation, Exception? primary,
        bool expectNoBoundRoute = false)
    {
        var failures = new List<Exception>();
        if (primary is not null)
        {
            failures.Add(primary);
            // All test barriers have been released. Stop selects terminal ownership for a call
            // whose response assertion failed, so its actual task can be joined rather than abandoned.
            try { await client.StopAsync(); } catch (Exception error) { failures.Add(error); }
        }
        if (invocation is not null)
        {
            try
            {
                if (expectNoBoundRoute) await RequireNoBoundRouteAsync(invocation);
                else await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception error) { if (!ReferenceEquals(error, primary)) failures.Add(error); }
        }
        ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private static ValueTask<int> InvokeUnary(IRpcContractChannel channel)
    {
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null),
            in request, RpcEmptyRequestCodec.Instance, channel.RuntimeContext.Codecs.GetCodec<int>(), null, default);
    }

    private static async Task RequireNoBoundRouteAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable &&
            error.DetailCode == SharpLinkErrorDetails.Unavailable.NoBoundRoute) { return; }
        throw new InvalidOperationException("Expected exact NoBoundRoute, not a generic transport failure.");
    }

    private static async Task AssertPostReservationRejectionAsync(bool clustered, bool invalidateToken)
    {
        var transport = ClientInvokerTestHelper.CreateTransport();
        var client = clustered
            ? ClientBuilderTestHelper.BuildStatic([new StaticEndpointConfiguration(CreateEndpoint("guard", 6501), transport)],
                builder => builder.UseCluster(options =>
                {
                    options.MinReadyEndpoints = 1;
                    options.MaxConnections = 1;
                    options.MaxConnectionsPerEndpoint = 1;
                }))
            : ClientBuilderTestHelper.Build(transport);
        await WithOwnedClientAsync(client, async () =>
        {
            var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
            var site = (SharpLinkClient.ContractCallSite)channel;
            var gate = (Lock)typeof(SharpLinkClient.ContractCallSite).GetField("_bindingGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(site)!;
            SharpLinkClient.ContractCallSite.BoundTarget target;
            lock (gate)
                target = ((SharpLinkClient.ContractCallSite.BoundTarget[])typeof(SharpLinkClient.ContractCallSite)
                    .GetField("_boundTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(site)!).Single();
            var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            client._callAdmissionReservedTestHook = connection =>
            {
                Ensure(ReferenceEquals(connection, target.Connection), "selection must retain the originally bound physical owner");
                reserved.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("guard reservation was not released");
            };
            Task<int>? invocation = null;
            Exception? primary = null;
            try
            {
                invocation = Task.Run(async () => await InvokeUnary(channel));
                await reserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(target.Connection.CallAdmissionReservationCount == 1 && target.Connection.PendingCalls.ActiveCount == 0,
                    "the control is after acquired selection but before pending publication");
                // Mutate the actual rejection gate outside the site lock, not a copied model or peer packet.
                if (invalidateToken) target.Connection.Binding.Publication.Invalidate(target.Token);
                else target.Connection.ObserveFatalFailureForAdmission();
                Ensure(target.Connection.State == ClientConnectionState.Ready && target.Connection.Session.IsConnected,
                    "the exact physical owner remains Ready/connected: this is not disconnect cleanup");
                Ensure(invalidateToken
                        ? !target.Connection.Binding.Publication.IsEligible(target.Token) && target.Connection.CanAcceptCalls
                        : target.Connection.Binding.Publication.IsEligible(target.Token) && target.Connection.HasObservedFatalFailureForAdmission,
                    "only the intended real token-retirement or fatal-admission gate has changed");
                release.Set();
                await RequireNoBoundRouteAsync(invocation);
                Ensure(target.Connection.CallAdmissionReservationCount == 0 && target.Connection.PendingCalls.ActiveCount == 0,
                    "post-reservation rejection must refund selection without installing a pending call");
                Ensure(!await transport.Connection.TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)),
                    "invalid token or fatal owner must reject before a business head reaches the transport");
            }
            catch (Exception error) { primary = error; }
            finally
            {
                release.Set();
                client._callAdmissionReservedTestHook = null;
            }
            await FinishOwnedInvocationAsync(client, invocation, primary, expectNoBoundRoute: true);
        });
    }

    private static async Task AssertSelectedCallSurvivesZeroRetiringRefreshAsync(
        SharpLinkClient client,
        ReviewRefreshTransportFactory factory)
    {
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
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

        Task<int>? unary = null;
        Exception? primary = null;
        try
        {
            unary = Task.Run(async () => await InvokeUnary(channel));
            var source = await selected.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await InjectRefreshAsync(sourceTransport, sourceTransport.Authority.ServerInstanceId, 2);
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            factory.ReleaseReplacement();
            await cut.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Ensure(source.State == ClientConnectionState.Ready,
                "an already-owned selection must keep its source physically Ready until registration/drain ownership transfers, including zero-retiring cluster budgets");
            Ensure(client.ReadyConnectionCount == 1,
                "the replacement must be the only published Ready connection after the eligibility cut");

            releaseSelection.Set();
            var request = await sourceTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await sourceTransport.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await unary.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                "a call selected before the cut must be formally registered and complete on its reserved source");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            releaseSelection.Set();
            factory.ReleaseReplacement();
            client._callAdmissionReservedTestHook = null;
            client._afterSessionRefreshEligibilitySwapTestHook = null;
        }
        await FinishOwnedInvocationAsync(client, unary, primary);
    }

    private static async Task AssertReplacementDisconnectReconnectsAsync(
        SharpLinkClient client,
        ReviewRefreshTransportFactory factory)
    {
        var eligibilityCut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client._afterSessionRefreshEligibilitySwapTestHook = () => eligibilityCut.TrySetResult();
        try
        {
            await InjectRefreshAsync(factory.GetConnection(0), factory.GetConnection(0).Authority.ServerInstanceId, 2);
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

        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var pending = InvokeUnary(channel).AsTask();
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
        using var writer = new PooledByteBufferWriter();
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
            connection.EnableManualContractBindings();
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
                    [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
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
