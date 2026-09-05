using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task ServerHeartbeatShouldKeepEqualityAndCloseOnlyTheStaleProviderSession()
    {
        var provider = new ManualTimeProvider();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTimeProvider(provider)
            .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))
            .UseTransport(new IdleListener())
            .Build();
        var runtimeContext = (SharpLinkRuntimeContext)(
            typeof(SharpLinkServer).GetField(
                "_runtimeContext",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!);
        var connections = (ServerConnectionRegistry)(
            typeof(SharpLinkServer).GetField(
                "_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!);
        var staleTransport = new TestTransportConnection();
        var healthyTransport = new TestTransportConnection();
        var staleSession = new RpcSession(
            staleTransport,
            RpcSessionTestFixture.ServerOptions(runtimeContext));
        var healthySession = new RpcSession(
            healthyTransport,
            RpcSessionTestFixture.ServerOptions(runtimeContext));
        RpcSessionTestFixture.CompleteHandshake(staleSession);
        RpcSessionTestFixture.CompleteHandshake(healthySession);
        var stale = new ServerConnectionState(
            staleSession,
            new RpcSessionGeneratedServerBridge(staleSession),
            CreateCallCancellations(runtimeContext),
            CancellationToken.None,
            provider);
        var healthy = new ServerConnectionState(
            healthySession,
            new RpcSessionGeneratedServerBridge(healthySession),
            CreateCallCancellations(runtimeContext),
            CancellationToken.None,
            provider);
        Ensure(stale.MarkReady(null) && healthy.MarkReady(null),
            "both provider-backed heartbeat sessions must begin Ready");
        Ensure(connections.TryAdd(staleSession.Id, stale) &&
               connections.TryAdd(healthySession.Id, healthy),
            "both heartbeat sessions must be published to the server connection table");
        var runHeartbeat = typeof(SharpLinkServer).GetMethod(
            "RunHeartbeatCheckLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server heartbeat wrapper");
        using var loopCancellation = new CancellationTokenSource();
        var heartbeat = (Task)runHeartbeat.Invoke(server, [loopCancellation.Token])!;

        try
        {
            Ensure(provider.ActiveTimerCount == 3,
                "two deadline schedulers plus the heartbeat loop must own three provider timers");
            Ensure(provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(5).Ticks,
                "the first server heartbeat check must be due at its provider interval");
            provider.Advance(TimeSpan.FromSeconds(5));
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
                "the first heartbeat check did not rearm its provider timer");
            Ensure(connections.Count == 2 && staleSession.IsConnected && healthySession.IsConnected,
                "sessions below the timeout must remain published and connected");

            provider.Advance(TimeSpan.FromSeconds(5));
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(15).Ticks,
                "the equality heartbeat check did not rearm its provider timer");
            Ensure(staleSession.TimeSinceLastActivity == TimeSpan.FromSeconds(10) &&
                   connections.Count == 2 && staleSession.IsConnected,
                "a server session exactly at heartbeat timeout must remain connected");
            healthySession.MarkActive();

            var staleClosed = GetConnectionCompletionTask(stale);
            provider.Advance(TimeSpan.FromSeconds(5));
            await staleClosed;
            Ensure(connections.Count == 1 &&
                   connections.TryGetValue(healthySession.Id, out var current) &&
                   ReferenceEquals(current, healthy),
                "the post-boundary check must remove only the stale session");
            Ensure(stale.LifecycleState == ServerConnectionLifecycleState.Closed &&
                   !staleSession.IsConnected,
                "the stale session must reach its single Closed terminal state");
            Ensure(healthy.LifecycleState == ServerConnectionLifecycleState.Ready &&
                   healthySession.IsConnected &&
                   healthySession.TimeSinceLastActivity == TimeSpan.FromSeconds(5),
                "refreshing one session must isolate it from another session's timeout");
        }
        finally
        {
            loopCancellation.Cancel();
            await heartbeat;
            connections.TryRemove(healthySession.Id, out _);
            connections.TryRemove(staleSession.Id, out _);
            await stale.CloseAsync();
            await healthy.CloseAsync();
            await stale.ServiceCleanupTask;
            await healthy.ServiceCleanupTask;
        }

        Ensure(provider.ActiveTimerCount == 0,
            "server heartbeat cancellation and connection close must release every provider timer");
    }

    [Test]
    public async Task DispatchObserverShouldSuppressOnlyExpectedConnectionClosure()
    {
        var loggerFactory = new CaptureLoggerFactory();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseLoggerFactory(loggerFactory)
            .UseTransport(new IdleListener())
            .Build();
        var awaitDispatch = typeof(SharpLinkServer).GetMethod(
            "AwaitDispatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server dispatch observer");
        var expectedClosure = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "Session is stopping.");

        await InvokeAwaitDispatchAsync(awaitDispatch, server, expectedClosure, requestId: 41);

        Ensure(loggerFactory.ErrorEntries.Count == 0,
            "normal session shutdown must not be reported as an unhandled dispatch error");

        var internalFailure = new SharpLinkException(
            SharpLinkErrorCode.Internal,
            "dispatch failed internally");
        await InvokeAwaitDispatchAsync(awaitDispatch, server, internalFailure, requestId: 42);
        Ensure(loggerFactory.ErrorEntries is [{ EventId.Id: LogEvents.Rpc.DispatchFailed } internalEntry] &&
               ReferenceEquals(internalEntry.Exception, internalFailure),
            "non-terminal SharpLink failures must remain observable as dispatch errors");

        var unexpectedFailure = new InvalidOperationException("unexpected dispatch failure");
        await InvokeAwaitDispatchAsync(awaitDispatch, server, unexpectedFailure, requestId: 43);
        Ensure(loggerFactory.ErrorEntries is
               [
               { EventId.Id: LogEvents.Rpc.DispatchFailed },
               { EventId.Id: LogEvents.Rpc.DispatchFailed } unexpectedEntry
               ] && ReferenceEquals(unexpectedEntry.Exception, unexpectedFailure),
            "ordinary unexpected failures must remain observable as dispatch errors");
    }

    [Test]
    public async Task SessionShutdownShouldNotHideAnUnexpectedSiblingCleanupFailure()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var connections = (ServerConnectionRegistry)(
            typeof(SharpLinkServer).GetField("_connectionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!);
        var unexpectedTransport = new ThrowingTransportConnection(
            "unexpected",
            new InvalidOperationException("unexpected sibling session cleanup failed"));
        var unexpectedSession = new RpcSession(
            unexpectedTransport,
            RpcSessionTestFixture.ServerOptions());
        var unexpected = new ServerConnectionState(
            unexpectedSession,
            new RpcSessionGeneratedServerBridge(unexpectedSession),
            CreateCallCancellations(),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
        connections.TryAdd(unexpected.Session.Id, unexpected);

        var expectedTransports = new List<ThrowingTransportConnection>();
        for (var index = 0; index < 64 && ReferenceEquals(connections.Values.First(), unexpected); index++)
        {
            var transport = new ThrowingTransportConnection(
                $"expected-{index}",
                new IOException("expected session transport closure"));
            expectedTransports.Add(transport);
            var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions());
            var connection = new ServerConnectionState(
                session,
                new RpcSessionGeneratedServerBridge(session),
                CreateCallCancellations(),
                CancellationToken.None,
                RpcSessionTestFixture.RuntimeContext.TimeProvider);
            connections.TryAdd(connection.Session.Id, connection);
        }
        Ensure(!ReferenceEquals(connections.Values.First(), unexpected),
            "the expected close must be first in the deterministic shutdown snapshot");

        var disposeSessions = CreatePrivateCall<Func<SharpLinkServer, Task>>(
            typeof(SharpLinkServer).GetMethod(
                "DisposeAllSessionsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server session shutdown path"));
        Exception? failure = null;
        try
        {
            await disposeSessions(server);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsMessage(failure, "unexpected sibling session cleanup failed"),
            "an expected sibling close must not hide an unexpected session cleanup failure");
        Ensure(unexpectedTransport.DisposeCount == 1 &&
               expectedTransports.All(static transport => transport.DisposeCount == 1),
            "parallel session shutdown must still dispose every transport");
    }

    [Test]
    public async Task FrameworkSupervisorShouldNotHideAnUnexpectedSiblingFailure()
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = Task.WhenAll(expected.Task, unexpected.Task);
        server.TrackFrameworkTask(mixed, "MixedServerWorker");
        await Task.Yield();
        expected.TrySetException(new IOException("expected framework transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected framework sibling failure"));

        Exception? failure = null;
        try
        {
            await server.StopAsync(TimeSpan.Zero);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsMessage(failure, "unexpected framework sibling failure"),
            "an expected framework close must not hide an unexpected sibling task failure");
    }
}
