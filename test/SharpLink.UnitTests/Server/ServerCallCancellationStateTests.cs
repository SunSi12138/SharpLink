using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallCancellationStateTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public void ModuleDrainingShouldCancelOnlyItsCooperativeInvocation()
    {
        using var moduleDraining = new CancellationTokenSource();
        var state = Rent(
            100,
            null,
            0,
            CancellationToken.None,
            CancellationToken.None,
            moduleDraining.Token,
            supportsCooperativeCancellation: true);

        moduleDraining.Cancel();

        Ensure(state.Reason == ServerCallCancellationReason.ModuleDraining,
            "module cancellation reason");
        Ensure(state.InvocationToken.IsCancellationRequested,
            "module drain cancels cooperative business code");
        Ensure(state.TryClaimModuleDrainResponse(), "module drain response is claimed once");
        Ensure(!state.TryClaimModuleDrainResponse(), "module drain response cannot be claimed twice");
        state.Dispose();
    }

    [Test]
    public void FirstCancellationSourceShouldWin()
    {
        using var connectionClosed = new CancellationTokenSource();
        using var serverStopping = new CancellationTokenSource();
        var state = Rent(
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            DeadlineAfter(TimeSpan.FromMinutes(1)),
            connectionClosed.Token,
            serverStopping.Token,
            supportsCooperativeCancellation: true);

        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "remote cancel should win");
        serverStopping.Cancel();
        connectionClosed.Cancel();

        Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel, "later cancellation must not replace the winner");
        Ensure(state.InvocationToken.IsCancellationRequested, "cooperative invocation token should be canceled");
        state.Dispose();
    }

    [Test]
    public async Task DeadlineTimerShouldSetDeadlineReason()
    {
        var state = Rent(
            2,
            DateTimeOffset.UtcNow.AddMilliseconds(25),
            DeadlineAfter(TimeSpan.FromMilliseconds(25)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state);

        await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded);

        Ensure(state.InvocationToken.IsCancellationRequested, "deadline should cancel the invocation token");
        Ensure(!state.TryClaimResponse(), "deadline must suppress a late response");
    }

    [Test]
    public async Task DeadlineReasonShouldBePublishedBeforeInvocationCallbacksRun()
    {
        var state = Rent(
            20,
            DateTimeOffset.UtcNow.AddMilliseconds(25),
            DeadlineAfter(TimeSpan.FromMilliseconds(25)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state);
        var observedReason = new TaskCompletionSource<ServerCallCancellationReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = state.InvocationToken.Register(
            () => observedReason.TrySetResult(state.Reason));

        var callbackReason = await observedReason.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Ensure(callbackReason == ServerCallCancellationReason.DeadlineExceeded,
            "business cancellation callbacks must observe the published deadline reason");
    }

    [Test]
    public async Task NonCooperativeDeadlineShouldNotCreateInvocationCancellationSource()
    {
        var state = Rent(
            21,
            DateTimeOffset.UtcNow.AddMilliseconds(25),
            DeadlineAfter(TimeSpan.FromMilliseconds(25)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        using var scheduledCall = Schedule(state);

        Ensure(!state.InvocationToken.CanBeCanceled,
            "non-cooperative calls should not allocate an invocation cancellation source");
        await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded);
        Ensure(!state.TryClaimResponse(), "non-cooperative late response must be suppressed");
    }

    [Test]
    public void FakeTimeSchedulerShouldExpireEqualDeadlinesTogetherKeepOrderAndPreserveCancellationWinner()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 4, timeProvider);
        var firstDeadline = RpcDeadline.Create(
            timeProvider.GetUtcNow().AddSeconds(1),
            timeProvider);
        var laterDeadline = RpcDeadline.Create(
            timeProvider.GetUtcNow().AddSeconds(2),
            timeProvider);
        var first = ServerCallCancellationState.Rent(
            101, firstDeadline, timeProvider,
            CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: true);
        var tied = ServerCallCancellationState.Rent(
            102, firstDeadline, timeProvider,
            CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        var later = ServerCallCancellationState.Rent(
            103, laterDeadline, timeProvider,
            CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        var canceled = ServerCallCancellationState.Rent(
            104, laterDeadline, timeProvider,
            CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: true);
        var states = new[] { first, tied, later, canceled };
        foreach (var state in states)
        {
            calls.Set(state.RequestId, state);
            scheduler.Register(state);
        }

        Ensure(timeProvider.ActiveTimerCount == 1,
            "one server connection scheduler must own exactly one provider timer");
        Ensure(canceled.TryCancel(ServerCallCancellationReason.RemoteCancel),
            "caller cancellation must claim its call before the deadline");
        timeProvider.Advance(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        Ensure(states.All(static state => state.Reason is ServerCallCancellationReason.None or
                                            ServerCallCancellationReason.RemoteCancel),
            "no server deadline may fire one provider tick early");

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Ensure(first.Reason == ServerCallCancellationReason.DeadlineExceeded &&
               tied.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "all calls sharing the earliest timestamp must expire in the same scan");
        Ensure(later.Reason == ServerCallCancellationReason.None,
            "the later deadline must remain live after the first scan");
        Ensure(canceled.Reason == ServerCallCancellationReason.RemoteCancel,
            "deadline scanning must not replace an earlier cancellation winner");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Ensure(later.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the later deadline must expire only at its own monotonic timestamp");
        Ensure(canceled.Reason == ServerCallCancellationReason.RemoteCancel,
            "a later exact deadline must remain a no-op after cancellation");

        foreach (var state in states)
        {
            Ensure(calls.TryRemove(state.RequestId, state), "scheduled call cleanup");
            state.Dispose();
        }
    }

    [Test]
    public void FakeTimeSchedulerDisposeShouldDisarmItsOwnedTimer()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var deadline = RpcDeadline.Create(
            timeProvider.GetUtcNow().AddSeconds(1),
            timeProvider);
        var state = ServerCallCancellationState.Rent(
            105, deadline, timeProvider,
            CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: true);
        calls.Set(state.RequestId, state);
        var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 1, timeProvider);
        scheduler.Register(state);

        Ensure(timeProvider.ActiveTimerCount == 1,
            "server scheduler must register one provider timer");
        scheduler.Dispose();
        Ensure(timeProvider.ActiveTimerCount == 0,
            "server scheduler disposal must remove its provider timer");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Ensure(state.Reason == ServerCallCancellationReason.None,
            "a disposed scheduler must not run its deadline callback");
        Ensure(!state.TryClaimResponse() &&
               state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the call's own exact-boundary claim guard must still reject late success");

        Ensure(calls.TryRemove(state.RequestId, state), "disposed scheduler call cleanup");
        state.Dispose();
    }

    [Test]
    public async Task UserCancellationBeforeDeadlineShouldRemainTheTerminalReason()
    {
        var state = Rent(
            23,
            DateTimeOffset.UtcNow.AddMilliseconds(40),
            DeadlineAfter(TimeSpan.FromMilliseconds(40)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state);

        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel),
            "user cancellation should claim the call");
        await Task.Delay(80);

        Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel,
            "a later deadline must not replace user cancellation");
    }

    [Test]
    public async Task DeadlineBeforeUserCancellationShouldRemainTheTerminalReason()
    {
        var state = Rent(
            24,
            DateTimeOffset.UtcNow.AddMilliseconds(20),
            DeadlineAfter(TimeSpan.FromMilliseconds(20)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state);

        await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded);

        Ensure(!state.TryCancel(ServerCallCancellationReason.RemoteCancel),
            "user cancellation must lose after the deadline is published");
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "deadline reason must remain stable");
    }

    [Test]
    public void ResponseClaimShouldUseMonotonicDeadlineInsteadOfUtcClock()
    {
        var state = Rent(
            22,
            DateTimeOffset.UtcNow.AddMinutes(1),
            Stopwatch.GetTimestamp() - 1,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);

        Ensure(!state.TryClaimResponse(), "elapsed monotonic deadline must suppress the response");
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "elapsed monotonic deadline reason");
        state.Dispose();
    }

    [Test]
    public void ServerStopAndConnectionCloseShouldHaveDistinctReasons()
    {
        using var firstConnection = new CancellationTokenSource();
        using var firstServer = new CancellationTokenSource();
        var serverState = Rent(
            3, null, 0, firstConnection.Token, firstServer.Token, supportsCooperativeCancellation: true);
        firstServer.Cancel();
        firstConnection.Cancel();
        Ensure(serverState.Reason == ServerCallCancellationReason.ServerStopping, "server stop reason");

        using var secondConnection = new CancellationTokenSource();
        using var secondServer = new CancellationTokenSource();
        var connectionState = Rent(
            4, null, 0, secondConnection.Token, secondServer.Token, supportsCooperativeCancellation: true);
        secondConnection.Cancel();
        secondServer.Cancel();
        Ensure(connectionState.Reason == ServerCallCancellationReason.ConnectionClosed, "connection close reason");

        serverState.Dispose();
        connectionState.Dispose();
    }

    [Test]
    public void ServerStopAndConnectionCloseRaceShouldPublishOneStableReason()
    {
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            using var connectionClosed = new CancellationTokenSource();
            using var serverStopping = new CancellationTokenSource();
            var state = Rent(
                iteration + 1,
                null,
                0,
                connectionClosed.Token,
                serverStopping.Token,
                supportsCooperativeCancellation: true);

            Parallel.Invoke(connectionClosed.Cancel, serverStopping.Cancel);

            Ensure(state.Reason is ServerCallCancellationReason.ConnectionClosed or
                    ServerCallCancellationReason.ServerStopping,
                "one infrastructure cancellation reason must win");
            Ensure(state.InvocationToken.IsCancellationRequested,
                "the invocation token must observe the winning infrastructure reason");
            state.Dispose();
        }
    }

    [Test]
    public void CompletedCallShouldIgnoreLaterCancellation()
    {
        using var connectionClosed = new CancellationTokenSource();
        var state = Rent(
            5, null, 0, connectionClosed.Token, CancellationToken.None, supportsCooperativeCancellation: true);

        Ensure(state.TryClaimResponse(), "normal completion should claim the terminal state");
        connectionClosed.Cancel();

        Ensure(state.Reason == ServerCallCancellationReason.Completed, "completed state must be stable");
        state.Dispose();
    }

    [Test]
    public void ThrowingUserCancellationCallbackShouldNotEscapeFrameworkCancellation()
    {
        var state = Rent(
            6, null, 0, CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);
        using var registration = state.InvocationToken.Register(static () => throw new InvalidOperationException("user callback"));

        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "framework cancellation should still succeed");
        Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel, "cancellation reason should remain observable");
        state.Dispose();
    }

    [Test]
    public void CancelResponseDeadlineAndDisposeRaceShouldNotCorruptPooledState()
    {
        for (var iteration = 1; iteration <= 100_000; iteration++)
        {
            var requestId = iteration;
            var state = Rent(
                requestId,
                null,
                0,
                CancellationToken.None,
                CancellationToken.None,
                supportsCooperativeCancellation: true);
            Ensure(state.TryAcquire(requestId), "cancel observer should acquire state lifetime");

            Parallel.Invoke(
                () =>
                {
                    try
                    {
                        state.TryCancel(ServerCallCancellationReason.RemoteCancel);
                    }
                    finally
                    {
                        state.ReleaseUse();
                    }
                },
                () =>
                {
                    state.TryClaimResponse();
                },
                () => state.TryCancel(ServerCallCancellationReason.DeadlineExceeded));

            state.Dispose();
        }
    }

    [Test]
    public void ResponseCallerCancellationAndDeadlineRacesShouldPublishOneTerminalReason()
    {
        for (var iteration = 1; iteration <= 100; iteration++)
        {
            using var callerState = Rent(
                iteration,
                null,
                0,
                CancellationToken.None,
                CancellationToken.None,
                supportsCooperativeCancellation: true);
            var callerRace = RaceResponseAndCancellation(
                callerState,
                ServerCallCancellationReason.RemoteCancel,
                $"P2-T01 iteration {iteration}");
            Ensure(callerRace.ResponseWon ^ callerRace.CancellationWon,
                $"P2-T01 iteration {iteration}: response and caller cancellation need one winner");
            Ensure(callerState.Reason == (callerRace.ResponseWon
                    ? ServerCallCancellationReason.Completed
                    : ServerCallCancellationReason.RemoteCancel),
                $"P2-T01 iteration {iteration}: terminal reason must match the winner");
            Ensure(!callerState.TryClaimResponse() &&
                   !callerState.TryCancel(ServerCallCancellationReason.RemoteCancel),
                $"P2-T01 iteration {iteration}: late terminal attempts must be ignored");

            using var deadlineState = Rent(
                10_000 + iteration,
                DateTimeOffset.UtcNow.AddMinutes(1),
                DeadlineAfter(TimeSpan.FromMinutes(1)),
                CancellationToken.None,
                CancellationToken.None,
                supportsCooperativeCancellation: true);
            var deadlineRace = RaceResponseAndCancellation(
                deadlineState,
                ServerCallCancellationReason.DeadlineExceeded,
                $"P2-T02 iteration {iteration}");
            Ensure(deadlineRace.ResponseWon ^ deadlineRace.CancellationWon,
                $"P2-T02 iteration {iteration}: response and deadline need one winner");
            Ensure(deadlineState.Reason == (deadlineRace.ResponseWon
                    ? ServerCallCancellationReason.Completed
                    : ServerCallCancellationReason.DeadlineExceeded),
                $"P2-T02 iteration {iteration}: terminal reason must match the winner");
            Ensure(!deadlineState.TryClaimResponse() &&
                   !deadlineState.TryCancel(ServerCallCancellationReason.DeadlineExceeded),
                $"P2-T02 iteration {iteration}: success must not be followed by a deadline error");

            using var futureDeadlineState = Rent(
                20_000 + iteration,
                DateTimeOffset.UtcNow.AddMinutes(1),
                DeadlineAfter(TimeSpan.FromMinutes(1)),
                CancellationToken.None,
                CancellationToken.None,
                supportsCooperativeCancellation: false);
            Ensure(futureDeadlineState.TryClaimResponse(),
                $"P2-T02 iteration {iteration}: a future deadline must not fire early");
            Ensure(futureDeadlineState.Reason == ServerCallCancellationReason.Completed &&
                   !futureDeadlineState.TryCancel(ServerCallCancellationReason.DeadlineExceeded),
                $"P2-T02 iteration {iteration}: completion before the deadline must stay final");
        }
    }

    [Test]
    public async Task DuplicateCancelLateResponseAndLateStreamCompleteShouldBeIdempotentAndLeaveNoResources()
    {
        var limiter = new LateResponseLogLimiter(TimeProvider.System.TimestampFrequency);
        var emittedDiagnostics = 0;
        const long diagnosticWindowStart = 1;
        using var pending = PendingRequestTableTestFixture.Create(2);

        for (var iteration = 1; iteration <= 100; iteration++)
        {
            using var state = Rent(
                30_000 + iteration,
                null,
                0,
                CancellationToken.None,
                CancellationToken.None,
                supportsCooperativeCancellation: true);
            Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel),
                $"P2-T08 iteration {iteration}: first cancel must win");
            Ensure(!state.TryCancel(ServerCallCancellationReason.RemoteCancel) &&
                   !state.TryClaimResponse(),
                $"P2-T08 iteration {iteration}: duplicate cancel and late response must be ignored");
            Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel,
                $"P2-T08 iteration {iteration}: duplicate events must not change the reason");
            Ensure(state.TryRecordAbandoned() && !state.TryRecordAbandoned(),
                $"P2-T08 iteration {iteration}: abandonment must be recorded once");

            var operation = pending.Rent(
                Int32Codec.Instance,
                PendingCallKind.Unary,
                deadline: default,
                CancellationToken.None,
                out var requestId);
            Ensure(pending.TryComplete(requestId, PendingCallCompletionReason.UserCancellation),
                $"P2-T08 iteration {iteration}: pending cancel must complete once");
            var emptyPayload = ReadOnlySequence<byte>.Empty;
            Ensure(!pending.Dispatch(requestId, ref emptyPayload) &&
                   !pending.TryComplete(requestId, PendingCallCompletionReason.RemoteStreamComplete),
                $"P2-T08 iteration {iteration}: late response/StreamComplete must not reclaim the slot");
            Ensure(await CaptureExceptionAsync(operation.AsValueTask().AsTask()) is OperationCanceledException,
                $"P2-T08 iteration {iteration}: the caller must observe the cancel terminal");
            Ensure(pending.Count == 0,
                $"P2-T08 iteration {iteration}: pending slot must be released");

            var streams = new StreamManager();
            var dispatcher = new CountingDispatcher();
            streams.Register(requestId, dispatcher);
            streams.CompleteStream(requestId, exception: null);
            streams.CompleteStream(requestId, exception: null);
            await streams.DispatchChunkAsync(
                requestId,
                new ReadOnlySequence<byte>(new byte[] { checked((byte)iteration) }));
            Ensure(dispatcher.CompleteCount == 1 && dispatcher.DispatchCount == 0,
                $"P2-T08 iteration {iteration}: a dispatcher must complete once and never be recreated");
            Ensure(streams.ActiveStreamCount == 0 && streams.DroppedStreamFrames == 1,
                $"P2-T08 iteration {iteration}: late stream data is one bounded diagnostic with zero streams");

            if (limiter.ShouldLog(diagnosticWindowStart + iteration, out _))
                emittedDiagnostics++;
            if (limiter.ShouldLog(diagnosticWindowStart + 100 + iteration, out _))
                emittedDiagnostics++;
        }

        Ensure(limiter.ShouldLog(
                diagnosticWindowStart + limiter.IntervalTimestampTicks + 1,
                out var suppressedDiagnostics),
            "P2-T08: the next diagnostic window must summarize suppressed late frames");
        emittedDiagnostics++;
        Ensure(emittedDiagnostics == 2 && suppressedDiagnostics == 199,
            "P2-T08: 200 late frames must produce two bounded diagnostics and summarize 199 suppressions");
        Ensure(pending.Count == 0, "P2-T08: all pending resources must finish at zero");
    }

    [Test]
    [NotInParallel]
    public void OldSnapshotShouldNotAcquireAReusedPooledState()
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var first = Rent(
            50, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        calls.Set(first.RequestId, first);
        var snapshot = new KeyValuePair<long, ServerCallCancellationState>[1];
        Ensure(calls.CopyEntries(snapshot) == 1, "snapshot count");
        Ensure(calls.TryRemove(first.RequestId, first), "remove old call");
        first.Dispose();

        var reused = Rent(
            51, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        Ensure(ReferenceEquals(snapshot[0].Value, reused),
            "the test must exercise the same pooled state instance");
        Ensure(!snapshot[0].Value.TryAcquire(snapshot[0].Key),
            "an old request ID must not acquire a new lease");
        reused.Dispose();
    }

    private static ServerCallCancellationState Rent(
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken connectionClosedToken,
        CancellationToken serverStoppingToken,
        bool supportsCooperativeCancellation)
        => Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            connectionClosedToken,
            serverStoppingToken,
            CancellationToken.None,
            supportsCooperativeCancellation);

    private static ServerCallCancellationState Rent(
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken connectionClosedToken,
        CancellationToken serverStoppingToken,
        CancellationToken moduleDrainingToken,
        bool supportsCooperativeCancellation)
        => ServerCallCancellationState.Rent(
            requestId,
            deadline is { } utcDeadline
                ? RpcDeadline.Create(utcDeadline, deadlineTimestamp)
                : default,
            TimeProvider.System,
            connectionClosedToken,
            serverStoppingToken,
            moduleDrainingToken,
            supportsCooperativeCancellation);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("condition was not reached");
            await Task.Delay(5);
        }
    }

    private static long DeadlineAfter(TimeSpan duration)
        => Stopwatch.GetTimestamp() +
           Math.Max(1L, (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));

    private static (bool ResponseWon, bool CancellationWon) RaceResponseAndCancellation(
        ServerCallCancellationState state,
        ServerCallCancellationReason cancellationReason,
        string scenario)
    {
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(initialState: false);
        var responseWon = false;
        var cancellationWon = false;
        var response = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            responseWon = state.TryClaimResponse();
        });
        var cancellation = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            cancellationWon = state.TryCancel(cancellationReason);
        });

        Ensure(ready.Wait(RaceCoordinationTimeout), $"{scenario}: workers must reach the start gate");
        start.Set();
        Ensure(Task.WaitAll([response, cancellation], RaceCoordinationTimeout),
            $"{scenario}: workers must finish within the race bound");
        return (responseWon, cancellationWon);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static ScheduledCall Schedule(ServerCallCancellationState state)
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        calls.Set(state.RequestId, state);
        var scheduler = new ServerCallDeadlineScheduler(
            calls,
            maxCalls: 1,
            TimeProvider.System);
        scheduler.Register(state);
        return new ScheduledCall(calls, scheduler, state);
    }

    private sealed class ScheduledCall(
        StripedLongMap<ServerCallCancellationState> calls,
        ServerCallDeadlineScheduler scheduler,
        ServerCallCancellationState state) : IDisposable
    {
        public void Dispose()
        {
            calls.TryRemove(state.RequestId, state);
            scheduler.Dispose();
            state.Dispose();
        }
    }

    private sealed class CountingDispatcher : IStreamDispatcher
    {
        private int _dispatchCount;
        private int _completeCount;

        internal int DispatchCount => Volatile.Read(ref _dispatchCount);
        internal int CompleteCount => Volatile.Read(ref _completeCount);

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            Interlocked.Increment(ref _dispatchCount);
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
            Interlocked.Increment(ref _completeCount);
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            Interlocked.Increment(ref _completeCount);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
