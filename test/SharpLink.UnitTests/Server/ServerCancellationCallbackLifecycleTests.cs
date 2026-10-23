using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public class ServerCancellationCallbackLifecycleTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ConnectionCloseMustRetainServiceUntilDetachedCallbackReturns(bool timer)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            var service = new TrackingService();
            var registration = CreateService(service);
            _ = LongRunningTestWorker.WaitForPhase(
                fixture.Connection.AcquireServiceAsync(registration, default).AsTask(), Bound);
            fixture.Trigger(timer);
            fixture.ReleaseInvocation();
            LongRunningTestWorker.WaitForPhase(fixture.Connection.CloseAsync().AsTask(), Bound);
            Ensure(service.DisposeCount == 0 && !fixture.Connection.ServiceCleanupTask.IsCompleted,
                "transport close and zero active calls must not dispose a service beneath its callback");
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.WaitForPhase(fixture.Connection.ServiceCleanupTask, Bound);
            Ensure(service.DisposeCount == 1, "connection service must release exactly once after callback completion");
            LongRunningTestWorker.WaitForPhase(registration.DisposeAsync().AsTask(), Bound);
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FailedPayloadCleanupMustStillAttemptConnectionServiceDisposal(bool timer)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            var service = new TrackingService();
            var registration = CreateService(service);
            _ = LongRunningTestWorker.WaitForPhase(
                fixture.Connection.AcquireServiceAsync(registration, default).AsTask(), Bound);
            fixture.Payload.ThrowOnDispose = true;
            fixture.Trigger(timer);
            fixture.ReleaseInvocation();
            LongRunningTestWorker.WaitForPhase(fixture.Connection.CloseAsync().AsTask(), Bound);
            Ensure(service.DisposeCount == 0, "even failed eventual cleanup must retain services during the callback");
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.Join(fixture.Connection.ServiceCleanupTask, Bound);
            Ensure(fixture.Connection.ServiceCleanupTask.IsFaulted,
                "best-effort resource cleanup must not erase the physical lifecycle failure");
            Ensure(service.DisposeCount == 1, "independent service cleanup must still be attempted exactly once");
            LongRunningTestWorker.WaitForPhase(registration.DisposeAsync().AsTask(), Bound);
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task StopBudgetMustNotDisposeRuntimeBeforeActiveOrRetiredCallback(bool retired)
        => LongRunningTestWorker.Run(() =>
        {
            var clock = new ManualTimeProvider();
            var listener = new Listener();
            var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseTimeProvider(clock).UseTransport(listener).Build();
            LongRunningTestWorker.WaitForPhase(server.StartAsync().AsTask(), Bound);
            using var fixture = new ServerCallbackOwnershipFixture(clock, server: server);
            var runtime = Field<SharpLinkRuntimeContext>(server, "_runtimeContext");
            var registry = Field<ServerConnectionRegistry>(server, "_connectionRegistry");
            Ensure(registry.TryAdd(fixture.Connection.Session.Id, fixture.Connection), "register the existing connection owner");
            var lifecycle = Field<SharpLinkServer.ServerLifecycleCoordinator>(server, "_lifecycle");
            try
            {
                fixture.Trigger(timer: false);
                if (retired)
                {
                    var disconnect = typeof(SharpLinkServer).GetMethod("DisconnectConnectionAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var pending = (ValueTask)disconnect.Invoke(server, [fixture.Connection])!;
                    LongRunningTestWorker.WaitForPhase(pending.AsTask(), Bound);
                    Ensure(registry.SnapshotOwned().Contains(fixture.Connection), "retired callback owner must still be tracked");
                }
                fixture.ReleaseInvocation();
                clock.Advance(TimeSpan.Zero); // Run the now-empty stale deadline scan before Stop's budget timer.
                var stop = server.StopAsync(TimeSpan.Zero).AsTask();
                Ensure(!stop.IsCompleted, "the physical callback must prevent successful immediate resource reclamation");
                var expected = clock.GetTimestamp() + server.ShutdownPlanForDiagnostics.CleanupBudget.Ticks;
                Ensure(SpinWait.SpinUntil(() => clock.EarliestTimerTimestamp == expected || stop.IsCompleted, Bound),
                    "Stop must register its unchanged observer budget before the test advances time");
                clock.Advance(server.ShutdownPlanForDiagnostics.CleanupBudget);
                LongRunningTestWorker.Join(stop, Bound);
                Ensure(stop.IsFaulted, "public Stop must terminate at its budget, not silently certify physical cleanup");
                var writer = runtime.Buffers.Rent();
                runtime.Buffers.Return(writer);
                LongRunningTestWorker.WaitForPhase(fixture.Connection.SessionTask, Bound);
                Ensure(!fixture.Connection.ServiceCleanupTask.IsCompleted,
                    "the unchanged public budget cannot release the held connection cleanup owner");
                fixture.CallbackRelease.Set();
                LongRunningTestWorker.WaitForPhase(fixture.Connection.ServiceCleanupTask, Bound);
                JoinObserver(lifecycle, "_shutdownCleanupObserver");
                JoinObserver(lifecycle, "_serviceCleanupObserver");
                EnsureBuffersDisposed(runtime);
            }
            finally
            {
                fixture.CallbackRelease.Set();
                fixture.ReleaseInvocation();
                LongRunningTestWorker.Join(server.StopAsync(TimeSpan.Zero).AsTask(), Bound);
                JoinObserver(lifecycle, "_shutdownCleanupObserver");
                JoinObserver(lifecycle, "_serviceCleanupObserver");
            }
        });

    private static void JoinObserver(object lifecycle, string name)
    {
        if (Field<Task?>(lifecycle, name) is { } task)
            LongRunningTestWorker.Join(task, Bound);
    }

    private static void EnsureBuffersDisposed(SharpLinkRuntimeContext runtime)
    {
        try
        {
            var writer = runtime.Buffers.Rent();
            runtime.Buffers.Return(writer);
            throw new Exception("global runtime resources must eventually release after physical callback completion");
        }
        catch (ObjectDisposedException) { }
    }

    private sealed class Listener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("cancelled accept cannot resume");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
