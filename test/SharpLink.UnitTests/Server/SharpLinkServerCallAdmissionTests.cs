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
    public async Task CallAdmissionShouldNotCrossTheServerDrainBoundary()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "admission-drain-race",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");

        var tryAcquire = CreatePrivateCall<Func<SharpLinkServer, ServerConnectionState, int>>(
            typeof(SharpLinkServer).GetMethod(
                "TryAcquireCall",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call admission path"));
        var setState = CreateInterlockedInt32Setter<SharpLinkServer>("_state");
        var callAdmission = typeof(SharpLinkServer).GetField("_callAdmission", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(server) ?? throw new Exception("cannot find Server call-admission owner");
        var globalActiveCalls = typeof(ServerCallAdmission).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find admission active-call counter");
        var connectionActiveCalls = typeof(ServerConnectionState).GetField(
            "_activeCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        const int running = 2;
        const int draining = 3;
        const int acquired = 0;
        const int delayVariants = 96;
        const int iterationsPerDelay = 2_000;
        using var phase = new Barrier(2);
        var admissionResult = -1;
        var witnessedLateAdmission = false;
        var worker = new Thread(() =>
        {
            for (var delay = 0; delay < delayVariants; delay++)
            {
                for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
                {
                    phase.SignalAndWait();
                    admissionResult = tryAcquire(server, connection);
                    phase.SignalAndWait();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpLink admission/drain race probe"
        };
        worker.Start();

        for (var delay = 0; delay < delayVariants; delay++)
        {
            for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
            {
                setState(server, running);
                globalActiveCalls.SetValue(callAdmission, 0);
                connectionActiveCalls.SetValue(connection, 0);
                admissionResult = -1;
                phase.SignalAndWait();
                Thread.SpinWait(delay);
                setState(server, draining);
                var drainObservedZeroCalls = (int)globalActiveCalls.GetValue(callAdmission)! == 0;
                phase.SignalAndWait();
                if (drainObservedZeroCalls && admissionResult == acquired)
                    witnessedLateAdmission = true;
            }
        }
        worker.Join();

        globalActiveCalls.SetValue(callAdmission, 0);
        connectionActiveCalls.SetValue(connection, 0);
        setState(server, draining);
        Ensure(!witnessedLateAdmission,
            "Stop observed zero active calls but a racing request was still admitted after the drain boundary");
        Ensure((int)globalActiveCalls.GetValue(callAdmission)! == 0, "global active-call counter rollback");
        Ensure(connection.ActiveCalls == 0, "connection active-call counter rollback");
        await connection.CloseAsync();
    }

    [Test]
    public async Task ConnectionAndServerCallCapacitiesShouldRejectIndependentlyAndRecover()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 2;
            })
            .UseTransport(new IdleListener())
            .Build();
        var firstInput = new Pipe();
        var firstOutput = new Pipe();
        var secondInput = new Pipe();
        var secondOutput = new Pipe();
        var thirdInput = new Pipe();
        var thirdOutput = new Pipe();
        await using var firstSession = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "capacity-first", firstInput.Reader, firstOutput.Writer,
            RpcSessionTestFixture.ServerOptions());
        await using var secondSession = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "capacity-second", secondInput.Reader, secondOutput.Writer,
            RpcSessionTestFixture.ServerOptions());
        await using var thirdSession = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "capacity-third", thirdInput.Reader, thirdOutput.Writer,
            RpcSessionTestFixture.ServerOptions());
        var firstConnection = CreateConnection(firstSession);
        var secondConnection = CreateConnection(secondSession);
        var thirdConnection = CreateConnection(thirdSession);
        Ensure(firstConnection.MarkReady(null), "first connection ready");
        Ensure(secondConnection.MarkReady(null), "second connection ready");
        Ensure(thirdConnection.MarkReady(null), "third connection ready");

        var tryAcquireMethod = typeof(SharpLinkServer).GetMethod(
            "TryAcquireCall",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call admission path");
        var tryAcquire = CreatePrivateCall<Func<SharpLinkServer, ServerConnectionState, int>>(
            tryAcquireMethod);
        var release = CreatePrivateCall<Action<SharpLinkServer, ServerConnectionState>>(
            typeof(SharpLinkServer).GetMethod(
                "ReleaseCall",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call release path"));
        var setState = CreateInterlockedInt32Setter<SharpLinkServer>("_state");
        const int running = 2;
        const int draining = 3;
        setState(server, running);
        var firstAcquired = false;
        var secondAcquired = false;
        var thirdAcquired = false;

        try
        {
            Ensure(server.MaxConcurrentCallsPerConnectionForDiagnostics == 1,
                "configured per-connection capacity");
            Ensure(server.MaxConcurrentCallsPerServerForDiagnostics == 2,
                "configured server-wide capacity");

            var belowCapacity = tryAcquire(server, firstConnection);
            firstAcquired = Enum.GetName(tryAcquireMethod.ReturnType, belowCapacity) == "Acquired";
            Ensure(firstAcquired, "the call below server capacity must be acquired");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 1 && firstConnection.ActiveCalls == 1,
                "below-capacity counters");
            server.AssertCallAccountingInvariant();
            firstConnection.AssertStateInvariant();

            var perConnectionRejection = tryAcquire(server, firstConnection);
            Ensure(Enum.GetName(tryAcquireMethod.ReturnType, perConnectionRejection) ==
                   "PerConnectionCapacityExhausted",
                "the same connection must report its own capacity reason");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 1 && firstConnection.ActiveCalls == 1,
                "per-connection rejection must not consume either counter");

            var atCapacity = tryAcquire(server, secondConnection);
            secondAcquired = Enum.GetName(tryAcquireMethod.ReturnType, atCapacity) == "Acquired";
            Ensure(secondAcquired, "the call exactly at server capacity must be acquired");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 2 && secondConnection.ActiveCalls == 1,
                "at-capacity counters");
            server.AssertCallAccountingInvariant();
            secondConnection.AssertStateInvariant();

            var serverRejection = tryAcquire(server, thirdConnection);
            Ensure(Enum.GetName(tryAcquireMethod.ReturnType, serverRejection) ==
                   "ServerCapacityExhausted",
                "the first call above the server limit must report server capacity");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 2 && thirdConnection.ActiveCalls == 0,
                "server rejection must roll back the provisional connection slot");
            Ensure(thirdConnection.LifecycleState == ServerConnectionLifecycleState.Ready,
                "capacity rejection must keep the healthy connection ready");
            server.AssertCallAccountingInvariant();
            thirdConnection.AssertStateInvariant();

            release(server, firstConnection);
            firstAcquired = false;
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 1 && firstConnection.ActiveCalls == 0,
                "releasing one call must restore one server and connection slot");

            var recovered = tryAcquire(server, thirdConnection);
            thirdAcquired = Enum.GetName(tryAcquireMethod.ReturnType, recovered) == "Acquired";
            Ensure(thirdAcquired,
                "the same healthy connection must acquire after server capacity is released");

            release(server, secondConnection);
            secondAcquired = false;
            release(server, thirdConnection);
            thirdAcquired = false;
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 0 &&
                   firstConnection.ActiveCalls == 0 &&
                   secondConnection.ActiveCalls == 0 &&
                   thirdConnection.ActiveCalls == 0,
                "all capacity counters must return to zero after release");
            server.AssertCallAccountingInvariant();
            firstConnection.AssertStateInvariant();
            secondConnection.AssertStateInvariant();
            thirdConnection.AssertStateInvariant();
        }
        finally
        {
            if (firstAcquired)
                release(server, firstConnection);
            if (secondAcquired)
                release(server, secondConnection);
            if (thirdAcquired)
                release(server, thirdConnection);
            setState(server, draining);
            await firstConnection.CloseAsync();
            await secondConnection.CloseAsync();
            await thirdConnection.CloseAsync();
        }
    }

    [Test]
    public async Task StopAndTerminalReleaseShouldPublishDrainAfterTheConnectionSlotIsReleased()
    {
        var listener = new BlockingListener();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stop-terminal-release", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");

        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired,
            "the active invocation must acquire both capacity slots before Stop");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "the admitted invocation must hold one global and one connection slot");

        connection.MarkDraining();
        var stopTask = server.StopAsync(TimeSpan.FromSeconds(2)).AsTask();
        await YieldUntilAsync(
            () => server.HealthStatus == SharpLinkHealthStatus.Draining,
            "StopAsync must publish draining before the terminal invocation release");
        Ensure(!server.CallsDrainedForDiagnostics.IsCompleted,
            "server call drain must remain unpublished while the paired slots are held");
        Ensure(!stopTask.IsCompleted,
            "StopAsync must not complete while either paired capacity slot is still held");

        server.ReleaseCall(connection);

        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
               server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
            "terminal release must return the paired global and connection counters to zero");
        Ensure(server.LastCallDrainSignalForDiagnostics is
        {
            GlobalActiveCalls: 0,
            PendingAdmissions: 0,
            ReleasingConnectionActiveCalls: 0
        },
            "the drain signal must observe the local connection slot at zero before publishing");
        server.AssertCallAccountingInvariant();
        connection.AssertStateInvariant();
        await connection.CloseAsync();
    }

#if DEBUG
    [Test]
    public async Task StopShouldWaitForPendingAdmissionBetweenConnectionAndGlobalSlots()
    {
        using var localSlotAcquired = new ManualResetEventSlim(initialState: false);
        using var allowGlobalAcquire = new ManualResetEventSlim(initialState: false);
        var listener = new BlockingListener();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "pending-admission-drain", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            CreateCallCancellations(),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider,
            afterLocalCallAdmission: () =>
            {
                localSlotAcquired.Set();
                allowGlobalAcquire.Wait();
            });
        Ensure(connection.MarkReady(null), "connection ready");

        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var admissionTask = LongRunningTestWorker.Run(() => server.TryAcquireCall(connection));
        try
        {
            Ensure(localSlotAcquired.Wait(TimeSpan.FromSeconds(2)),
                "the deterministic probe must observe the local slot before global admission");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 1 &&
                   connection.ActiveCalls == 1 &&
                   server.ActiveCallCountForDiagnostics == 0,
                "the pending admission must cover the local-only transfer window");

            var stopTask = server.StopAsync(TimeSpan.FromSeconds(2)).AsTask();
            await YieldUntilAsync(
                () => server.HealthStatus == SharpLinkHealthStatus.Draining,
                "StopAsync must close admission before the local-only transfer resumes");
            connection.MarkDraining();
            Ensure(!server.CallsDrainedForDiagnostics.IsCompleted && !stopTask.IsCompleted,
                "StopAsync must wait for the pending local-only admission rather than observing global zero");

            allowGlobalAcquire.Set();
            var admission = await admissionTask.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(admission == ServerCallAdmissionResult.Unavailable,
                "an admission that crosses the drain boundary must release instead of publishing a call");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
            await runTask.WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 0 &&
                   connection.ActiveCalls == 0,
                "the pending admission and both capacity slots must return to zero exactly once");
            Ensure(server.LastCallDrainSignalForDiagnostics is
            {
                GlobalActiveCalls: 0,
                PendingAdmissions: 0,
                ReleasingConnectionActiveCalls: 0
            },
                "the final drain signal must publish only after the paused local slot is released");
            server.AssertCallAccountingInvariant();
            connection.AssertStateInvariant();
        }
        finally
        {
            allowGlobalAcquire.Set();
            await LongRunningTestWorker.JoinAsync(admissionTask, TimeSpan.FromSeconds(2));
            var admission = await admissionTask;
            if (admission == ServerCallAdmissionResult.Acquired)
                server.ReleaseCall(connection);
            await connection.CloseAsync();
        }
    }
#endif

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledOrDeadlineExceededCallsShouldReleaseCapacityAndRecover(
        bool deadlineExceeded)
    {
        var output = new Pipe();
        var stub = new CancelThenRecoverStub();
        await using var harness = new ServerDispatchHarness(
            stub, output.Writer, maxSendQueueBytes: 1024);
        const long cancelledRequestId = 51;

        var cancelledDispatch = harness.Dispatch(cancelledRequestId, ProtocolV2FrameFlags.Cancellable);
        await stub.FirstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
            "an asynchronous invocation must hold both capacity slots");
        Ensure(harness.Connection.CallCancellations.TryCapture(
                   cancelledRequestId,
                   static (requestId, state) => state.CaptureLease(requestId),
                   out var callLease) &&
               callLease.TryAcquire(),
            "the live invocation must publish cancellable call state");
        var callState = callLease.State;
        try
        {
            var reason = deadlineExceeded
                ? ServerCallCancellationReason.DeadlineExceeded
                : ServerCallCancellationReason.RemoteCancel;
            Ensure(callState.TryCancel(reason),
                "the selected cancellation source must win the live invocation");
            Ensure(callState.Reason == reason,
                "the cancellation reason must be visible before invocation cleanup");
        }
        finally
        {
            callLease.ReleaseUse();
        }

        await cancelledDispatch.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "cancellation or deadline completion must release global and connection slots");
        Ensure(!harness.Connection.CallCancellations.TryGetValue(cancelledRequestId, out _),
            "completed cancellation state must be removed before capacity is reusable");
        Ensure(harness.Connection.LifecycleState == ServerConnectionLifecycleState.Ready &&
               harness.Session.IsConnected,
            "cancellation or deadline must not close the healthy connection");

        var recoveredDispatch = harness.Dispatch(52, ProtocolV2FrameFlags.None);
        Ensure(recoveredDispatch.IsCompletedSuccessfully,
            "the next invocation must reacquire the released capacity synchronously");
        await recoveredDispatch;
        Ensure(stub.InvocationCount == 2,
            "a recovered call must reach the service on the same connection");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "the recovered call must also release both counters");

        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await output.Reader.CompleteAsync();
    }
}
