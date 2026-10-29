using System.Linq;
using System.IO.Pipelines;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

[NotInParallel]
public class ConnectionCancellationFanoutTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public void TwoRegistrationsCoverManyCallsWithoutHiddenPerCallInfrastructureRegistrations()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 1024, TimeProvider.System);
        var callbacks = new List<Action>();
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token,
            register: (_, callback, state) => { callbacks.Add(() => callback(state)); return default; });
        var states = new List<ServerCallCancellationState>();
        try
        {
            for (var id = 1; id <= 100; id++)
            {
                var state = Rent(id, server.Token, connection.Token);
                states.Add(state);
                fanout.Publish(state, deadlines);
            }
            Ensure(callbacks.Count == 2, "exactly two connection registrations, independent of call count");
            server.Cancel(); connection.Cancel();
            Ensure(states.All(s => !s.InvocationToken.IsCancellationRequested),
                "with spy registrations not installed, no hidden per-call infrastructure registration may fire");
            callbacks[0]();
            Ensure(states.All(s => s.Reason == ServerCallCancellationReason.ServerStopping &&
                s.InvocationToken.IsCancellationRequested), "one connection callback must notify all calls");
            callbacks[1]();
            Ensure(states.All(s => s.Reason == ServerCallCancellationReason.ServerStopping), "first reason wins");
        }
        finally { foreach (var state in states) Remove(calls, state); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationBetweenRentAndPublicationCannotBeLost(bool stopServer)
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var snapshotEntered = new ManualResetEventSlim();
        using var continueSnapshot = new ManualResetEventSlim();
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token,
            capture: () => { var result = Snapshot(calls); snapshotEntered.Set(); Wait(continueSnapshot); return result; });
        var state = Rent(1, server.Token, connection.Token);
        var cancel = Task.Run(() => (stopServer ? server : connection).Cancel());
        try
        {
            Wait(snapshotEntered); // Empty snapshot captured while the rented call is still private.
            fanout.Publish(state, deadlines);
            Ensure(state.InvocationToken.IsCancellationRequested, "postpublication check must catch missed snapshot");
            Ensure(state.Reason == (stopServer ? ServerCallCancellationReason.ServerStopping :
                ServerCallCancellationReason.ConnectionClosed), "source reason");
        }
        finally { continueSnapshot.Set(); await cancel.WaitAsync(Watchdog); Remove(calls, state); }
    }

    [Test]
    public async Task CancellationAfterSetBeforePostcheckUsesExistingStripeOrdering()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        using var afterSet = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var clock = new ProbeTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, clock);
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token);
        var state = Rent(2, server.Token, connection.Token, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        clock.OnTimestamp = () => { afterSet.Set(); Wait(resume); };
        var publish = Task.Run(() => fanout.Publish(state, deadlines));
        try
        {
            Wait(afterSet); // Deadline registration only runs after map.Set.
            clock.OnTimestamp = null;
            connection.Cancel();
            Ensure(state.Reason == ServerCallCancellationReason.ConnectionClosed, "snapshot sees published state");
        }
        finally { resume.Set(); await publish.WaitAsync(Watchdog); Remove(calls, state); }
    }

    [Test]
    public void AlreadyCanceledSourceOrderingAndExpiredDeadlineRemainUnchanged()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        using var module = new CancellationTokenSource();
        server.Cancel(); connection.Cancel();
        using (var state = Rent(1, server.Token, connection.Token))
            Ensure(state.Reason == ServerCallCancellationReason.ServerStopping, "already canceled server precedes connection");
        module.Cancel();
        using (var state = Rent(2, server.Token, connection.Token, module.Token))
            Ensure(state.Reason == ServerCallCancellationReason.ModuleDraining, "already canceled module remains first");
        var time = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), time);
        time.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
        using var expired = Rent(3, server.Token, connection.Token, module.Token, time, deadline);
        Ensure(expired.Reason == ServerCallCancellationReason.DeadlineExceeded, "expired deadline promotes every new terminal claim");
    }

    [Test]
    public void LateNoncooperativeTrackingHasNoInvocationCtsAndStillClaimsClose()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token);
        var state = Rent(1, server.Token, connection.Token, cooperative: false);
        try
        {
            Ensure(!state.InvocationToken.CanBeCanceled, "late tracking must not invent a new user token");
            connection.Cancel(); fanout.Publish(state, deadlines);
            Ensure(state.Reason == ServerCallCancellationReason.ConnectionClosed && !state.TryClaimResponse(), "late close suppresses response");
        }
        finally { Remove(calls, state); }
    }

    [Test]
    public void ModuleAndRemoteCancellationRemainIsolatedAndCompletedCallsStayCompleted()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        using var module = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token);
        var a = Rent(1, server.Token, connection.Token, module.Token);
        var b = Rent(2, server.Token, connection.Token);
        var c = Rent(3, server.Token, connection.Token);
        try
        {
            fanout.Publish(a, deadlines); fanout.Publish(b, deadlines); fanout.Publish(c, deadlines);
            module.Cancel();
            Ensure(a.InvocationToken.IsCancellationRequested && !b.InvocationToken.IsCancellationRequested, "module isolation");
            b.TryCancel(ServerCallCancellationReason.RemoteCancel); c.TryClaimResponse(); server.Cancel();
            Ensure(a.Reason == ServerCallCancellationReason.ModuleDraining && b.Reason == ServerCallCancellationReason.RemoteCancel &&
                c.Reason == ServerCallCancellationReason.Completed && !c.InvocationToken.IsCancellationRequested, "existing terminal winners preserved");
        }
        finally { Remove(calls, a); Remove(calls, b); Remove(calls, c); }
    }

    [Test]
    public async Task StaleSnapshotCannotCancelReusedSameIdAndOldUserTokenIsNotReused()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        using var captured = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token,
            capture: () => { var result = Snapshot(calls); captured.Set(); Wait(resume); return result; });
        var old = Rent(7, server.Token, connection.Token);
        var oldToken = old.InvocationToken;
        fanout.Publish(old, deadlines);
        var cancel = Task.Run(connection.Cancel);
        ServerCallCancellationState? next = null;
        try
        {
            Wait(captured); old.TryClaimResponse(); Remove(calls, old);
            next = Rent(7, CancellationToken.None, CancellationToken.None);
            Ensure(ReferenceEquals(old, next), "controlled pool reuse must exercise same object and same request ID");
            resume.Set(); await cancel.WaitAsync(Watchdog);
            Ensure(!next.InvocationToken.IsCancellationRequested, "stale snapshot must not notify new generation");
            next.TryCancel(ServerCallCancellationReason.RemoteCancel);
            Ensure(!oldToken.IsCancellationRequested, "user-retained old CTS/token must remain independent");
        }
        finally { resume.Set(); await cancel.WaitAsync(Watchdog); next?.Dispose(); }
    }

    [Test]
    public async Task BlockingCallbackPinsOnlyItsPhysicalOwnerAndDisposeNeverWaitsForFanout()
    {
        using var source = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        var a = Rent(1, source.Token, CancellationToken.None);
        var b = Rent(2, source.Token, CancellationToken.None);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        a.AttachPayloadOwner(buffers, buffers.Rent());
        var bLease = b.CaptureLease(2);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => new List<ServerCallCancellationLease> { a.CaptureLease(1), bLease });
        fanout.Publish(a, deadlines); fanout.Publish(b, deadlines);
        using var registration = a.InvocationToken.Register(() => { entered.Set(); Wait(resume); });
        var cancel = Task.Run(source.Cancel);
        try
        {
            Wait(entered); Remove(calls, a); Remove(calls, b);
            Ensure(a.HasPayloadOwnerForDiagnostics, "blocked callback must retain the physical payload owner");
            Ensure(!bLease.TryAcquire(), "snapshot must not pin every later call behind one blocked callback");
            await Task.Run(fanout.Dispose).WaitAsync(Watchdog);
            Ensure(!cancel.IsCompleted, "Unregister must return without waiting for in-flight callback");
        }
        finally { resume.Set(); await cancel.WaitAsync(Watchdog); }
        Ensure(!a.HasPayloadOwnerForDiagnostics, "physical owner returns after callback's final lease release");
    }

    [Test]
    public void ThrowingAndReentrantCallbacksDoNotStopRemainingNotifications()
    {
        using var source = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        var a = Rent(1, source.Token, CancellationToken.None);
        var b = Rent(2, source.Token, CancellationToken.None);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => new List<ServerCallCancellationLease> { a.CaptureLease(1), b.CaptureLease(2) });
        fanout.Publish(a, deadlines); fanout.Publish(b, deadlines);
        using var registration = a.InvocationToken.Register(() =>
        {
            fanout.Dispose(); calls.TryRemove(1, a); a.Dispose();
            throw new InvalidOperationException("application callback");
        });
        source.Cancel();
        Ensure(b.InvocationToken.IsCancellationRequested, "throw/reentrant disposal must not stop later notifications");
        Remove(calls, b);
    }

    [Test]
    public void PartialRegistrationFailureUnregistersEarlierCallback()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        var registrations = 0; var captures = 0;
        Throws(() => new ServerConnectionCallCancellation(calls, server.Token, connection.Token,
            register: (token, callback, state) => ++registrations == 2
                ? throw new InvalidOperationException("injected registration failure") : token.UnsafeRegister(callback, state),
            capture: () => { captures++; return Snapshot(calls); }));
        server.Cancel(); connection.Cancel();
        Ensure(registrations == 2 && captures == 0, "partly constructed owner must not remain rooted by first source");
    }

    [Test]
    public void SnapshotFailureEscapesWithoutClaimingNotificationOrAcquiringLeases()
    {
        using var source = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => throw new InvalidOperationException("injected snapshot failure"));
        var state = Rent(1, source.Token, CancellationToken.None); var lease = state.CaptureLease(1);
        fanout.Publish(state, deadlines);
        Throws(source.Cancel);
        Ensure(state.Reason == ServerCallCancellationReason.None, "capture failure must not pretend full notification");
        Remove(calls, state);
        Ensure(!lease.TryAcquire(), "failed capture must leak no acquired generation");
    }

    [Test]
    public void IndividualTerminalFailureReleasesLeaseAndContinuesFanout()
    {
        using var source = new CancellationTokenSource();
        var clock = new ProbeTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, clock);
        var a = Rent(1, source.Token, CancellationToken.None, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        var b = Rent(2, source.Token, CancellationToken.None);
        var aLease = a.CaptureLease(1);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => new List<ServerCallCancellationLease> { aLease, b.CaptureLease(2) });
        fanout.Publish(a, deadlines); fanout.Publish(b, deadlines);
        clock.OnTimestamp = () => throw new InvalidOperationException("injected timestamp failure");
        Throws(source.Cancel);
        Ensure(b.InvocationToken.IsCancellationRequested, "one framework exception must not truncate fanout");
        Remove(calls, a); Remove(calls, b);
        Ensure(!aLease.TryAcquire(), "failed terminal callback must release its lease");
    }

    [Test]
    public void PublicationFailureRollsBackMapAndOwnedGeneration()
    {
        using var source = new CancellationTokenSource();
        var clock = new ProbeTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, clock);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None);
        var state = Rent(1, source.Token, CancellationToken.None, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        var lease = state.CaptureLease(1);
        clock.OnTimestamp = () => throw new InvalidOperationException("injected publication failure");
        Throws(() => fanout.Publish(state, deadlines));
        Ensure(!calls.TryGetValue(1, out _) && !lease.TryAcquire(), "unreturned factory state must be removed and disposed");
    }

    [Test]
    public void RentFailureRemovesModuleRegistrationBeforeRethrowing()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var module = new CancellationTokenSource();
        var clock = new ProbeTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromHours(1), clock);
        var calls = 0;
        clock.OnTimestamp = () => { calls++; throw new InvalidOperationException("injected rent failure"); };
        Throws(() => Rent(1, source.Token, CancellationToken.None, module.Token, clock, deadline));
        var before = calls; module.Cancel();
        Ensure(calls == before, "failed Rent must unregister module callback, not leave an ownerless pooled state");
    }

    [Test]
    public void PostpublishTerminalFailureRollsBackWhileHoldingGeneration()
    {
        using var source = new CancellationTokenSource();
        var clock = new ProbeTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, clock);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => new List<ServerCallCancellationLease>());
        var state = Rent(1, source.Token, CancellationToken.None, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        var lease = state.CaptureLease(1);
        var timestamps = 0;
        clock.OnTimestamp = () =>
        {
            if (++timestamps == 1) source.Cancel();
            else throw new InvalidOperationException("postpublication terminal failure");
        };
        Throws(() => fanout.Publish(state, deadlines));
        Ensure(timestamps >= 2 && !calls.TryGetValue(1, out _) && !lease.TryAcquire(),
            "cold acquired generation must be rolled back, then released exactly once");
    }

    [Test]
    public void RetiredPublishLeaseMustNeverRemoveOrDisposeReusedGeneration()
    {
        using var source = new CancellationTokenSource();
        var clock = new ProbeTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, clock);
        using var fanout = new ServerConnectionCallCancellation(calls, source.Token, CancellationToken.None,
            capture: () => new List<ServerCallCancellationLease>());
        var state = Rent(1, source.Token, CancellationToken.None, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        ServerCallCancellationState? next = null;
        clock.OnTimestamp = () =>
        {
            clock.OnTimestamp = null;
            Remove(calls, state);
            next = Rent(1, CancellationToken.None, CancellationToken.None);
            Ensure(ReferenceEquals(state, next), "same object/ID reuse required for ABA negative control");
            calls.Set(1, next);
            source.Cancel();
        };
        Throws(() => fanout.Publish(state, deadlines));
        Ensure(next is not null && calls.TryGetValue(1, out var found) && ReferenceEquals(next, found) &&
            next.Reason == ServerCallCancellationReason.None, "failed acquisition must not touch new generation");
        var lease = next!.CaptureLease(1);
        Ensure(lease.TryAcquire(), "new generation must remain alive"); lease.ReleaseUse();
        Remove(calls, next);
    }

    [Test]
    public void ConstructorReentrantDisposeCannotInstallRegistrationsAfterCleanup()
    {
        using var server = new CancellationTokenSource();
        using var connection = new CancellationTokenSource();
        var calls = new StripedLongMap<ServerCallCancellationState>();
        var captures = 0;
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token,
            register: (token, callback, owner) =>
            {
                ((ServerConnectionCallCancellation)owner).Dispose();
                return token.UnsafeRegister(callback, owner);
            }, capture: () => { captures++; return Snapshot(calls); });
        server.Cancel(); connection.Cancel();
        Ensure(captures == 0, "registrations returning after reentrant disposal must be unregistered");
    }

    [Test]
    public void LinkedConnectionForceStopStillSelectsServerStopping()
    {
        using var server = new CancellationTokenSource();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(server.Token);
        var calls = new StripedLongMap<ServerCallCancellationState>();
        using var deadlines = new ServerCallDeadlineScheduler(calls, 8, TimeProvider.System);
        using var fanout = new ServerConnectionCallCancellation(calls, server.Token, connection.Token);
        var state = Rent(1, server.Token, connection.Token);
        try
        {
            fanout.Publish(state, deadlines); server.Cancel();
            Ensure(connection.IsCancellationRequested && state.Reason == ServerCallCancellationReason.ServerStopping,
                "infrastructure registration must precede the linked connection's notification in shutdown");
        }
        finally { Remove(calls, state); }
    }

    [Test]
    public async Task UserCallbackCanReenterConnectionCloseAndServiceCleanupWithoutSelfDeadlock()
    {
        using var source = new CancellationTokenSource();
        var connection = CreateConnection(source.Token);
        var call = Rent(1, source.Token, connection.ConnectionToken);
        connection.InfrastructureCancellation.Publish(call, connection.DeadlineScheduler);
        var completed = false;
        using var registration = call.InvocationToken.Register(() =>
        {
            connection.CloseAsync().AsTask().WaitAsync(Watchdog).GetAwaiter().GetResult();
            connection.ServiceCleanupTask.WaitAsync(Watchdog).GetAwaiter().GetResult();
            completed = true;
        });
        await Task.Run(source.Cancel).WaitAsync(Watchdog + Watchdog);
        Ensure(completed && connection.LifecycleState == ServerConnectionLifecycleState.Closed,
            "new registration cleanup must not join the callback which is waiting for connection close");
        Remove(connection.CallCancellations, call);
    }

    [Test]
    public async Task FrameworkNotificationFailureUsesExistingConnectionCloseErrorChannel()
    {
        var connection = CreateConnection(CancellationToken.None);
        var clock = new ProbeTimeProvider();
        var call = Rent(1, CancellationToken.None, connection.ConnectionToken, clock: clock,
            deadline: RpcDeadline.Create(TimeSpan.FromHours(1), clock));
        var lease = call.CaptureLease(1);
        connection.InfrastructureCancellation.Publish(call, connection.DeadlineScheduler);
        clock.OnTimestamp = () => throw new InvalidOperationException("injected close notification failure");
        var failed = false;
        try { await connection.CloseAsync().AsTask().WaitAsync(Watchdog); }
        catch (AggregateException) { failed = true; }
        Ensure(failed && connection.SessionTask.IsFaulted,
            "direct close must preserve the existing failure channel, not claim full notification");
        Remove(connection.CallCancellations, call);
        Ensure(!lease.TryAcquire(), "failed close notification must release the call lease");
        await connection.ServiceCleanupTask.WaitAsync(Watchdog);
    }

    private static ServerConnectionState CreateConnection(CancellationToken server)
    {
        var input = new Pipe(); var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(Guid.NewGuid().ToString("N"), input.Reader, output.Writer,
            () => ValueTask.CompletedTask);
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        return new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(), server,
            RpcSessionTestFixture.RuntimeContext.TimeProvider, serverStoppingToken: server);
    }

    private static ServerCallCancellationState Rent(long id, CancellationToken server, CancellationToken connection,
        CancellationToken module = default, TimeProvider? clock = null, RpcDeadline deadline = default, bool cooperative = true)
        => ServerCallCancellationState.RentConnectionTracked(id, deadline, clock ?? TimeProvider.System,
            connection, server, module, cooperative);
    private static List<ServerCallCancellationLease> Snapshot(StripedLongMap<ServerCallCancellationState> calls)
        => calls.CaptureEntries(static (id, state) => state.CaptureLease(id));
    private static void Remove(StripedLongMap<ServerCallCancellationState> calls, ServerCallCancellationState state)
    { calls.TryRemove(state.RequestId, state); state.Dispose(); }
    private static void Wait(ManualResetEventSlim signal)
    { Ensure(signal.Wait(Watchdog), "coordination watchdog"); }
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    { try { action(); } catch (Exception) { return; } throw new InvalidOperationException("expected failure"); }
    private sealed class ProbeTimeProvider : TimeProvider
    {
        internal Action? OnTimestamp;
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return TimeProvider.System.GetTimestamp(); }
    }
}
