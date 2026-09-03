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
    public void DeadlineTimerShouldSetDeadlineReason()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            2,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(25), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state, timeProvider);

        timeProvider.Advance(TimeSpan.FromMilliseconds(25));

        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "deadline scheduler should publish the deadline reason at the configured boundary");
        Ensure(state.InvocationToken.IsCancellationRequested, "deadline should cancel the invocation token");
        Ensure(!state.TryClaimResponse(), "deadline must suppress a late response");
    }

    [Test]
    public async Task DeadlineReasonShouldBePublishedBeforeInvocationCallbacksRun()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            20,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(25), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state, timeProvider);
        var observedReason = new TaskCompletionSource<ServerCallCancellationReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = state.InvocationToken.Register(
            () => observedReason.TrySetResult(state.Reason));

        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        var callbackReason = await observedReason.Task;

        Ensure(callbackReason == ServerCallCancellationReason.DeadlineExceeded,
            "business cancellation callbacks must observe the published deadline reason");
    }

    [Test]
    public void NonCooperativeDeadlineShouldNotCreateInvocationCancellationSource()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            21,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(25), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        using var scheduledCall = Schedule(state, timeProvider);

        Ensure(!state.InvocationToken.CanBeCanceled,
            "non-cooperative calls should not allocate an invocation cancellation source");
        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the scheduler should publish the non-cooperative deadline reason at the configured boundary");
        Ensure(!state.TryClaimResponse(), "non-cooperative late response must be suppressed");
    }

    [Test]
    public void FakeTimeSchedulerShouldExpireEqualDeadlinesTogetherKeepOrderAndPreserveCancellationWinner()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 4, timeProvider);
        var firstDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var laterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(2), timeProvider);
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
    public void StreamDataAfterExpiredTimestampShouldLoseWithoutSchedulerCallback()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            106,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        try
        {
            Ensure(state.TryAcceptStreamData(),
                "client-stream data before the boundary should remain admissible");
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
            Ensure(!state.TryAcceptStreamData(),
                "client-stream data at/after the boundary must be rejected without waiting for the scheduler");
            Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                "the data-path gate should publish DeadlineExceeded as the terminal reason");
        }
        finally
        {
            state.Dispose();
        }
    }

    [Test]
    public void FakeTimeSchedulerDisposeShouldDisarmItsOwnedTimer()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
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
    public void UserCancellationBeforeDeadlineShouldRemainTheTerminalReason()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            23,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(40), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state, timeProvider);

        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel),
            "user cancellation should claim the call");
        timeProvider.Advance(TimeSpan.FromMilliseconds(40));

        Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel,
            "a later deadline must not replace user cancellation");
    }

    [Test]
    public void DeadlineBeforeUserCancellationShouldRemainTheTerminalReason()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            24,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(20), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state, timeProvider);

        timeProvider.Advance(TimeSpan.FromMilliseconds(20));
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "deadline scheduler should publish the winner before later user cancellation");

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
            var stateLease = state.CaptureLease(requestId);
            Ensure(stateLease.TryAcquire(), "cancel observer should acquire state lifetime");

            Parallel.Invoke(
                () =>
                {
                    try
                    {
                        state.TryCancel(ServerCallCancellationReason.RemoteCancel);
                    }
                    finally
                    {
                        stateLease.ReleaseUse();
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
    // The assertion forces reuse through the process-wide ServerCallCancellationState pool.
    [NotInParallel]
    public void OldSnapshotFromAnotherMapShouldNotAcquireAReusedSameIdState()
        => AssertOldSnapshotCannotAcquireReusedSameIdState(reuseSameMap: false);

    [Test]
    public async Task TryCaptureShouldKeepTheEntryStableUntilItsLeaseProjectionCompletes()
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var state = Rent(
            49, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        calls.Set(state.RequestId, state);
        using var captureEntered = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();

        var capture = LongRunningTestWorker.Run(() => calls.TryCapture(
            state.RequestId,
            (requestId, capturedState) =>
            {
                captureEntered.Set();
                releaseCapture.Wait();
                return capturedState.CaptureLease(requestId);
            },
            out var lease)
            ? lease
            : default);
        Task<bool>? remove = null;
        var capturedLease = default(ServerCallCancellationLease);
        var leaseAcquired = false;
        try
        {
            Ensure(captureEntered.Wait(RaceCoordinationTimeout),
                "the lease projection must execute while its stripe is locked");
            remove = LongRunningTestWorker.Run(() => calls.TryRemove(state.RequestId, state));
            await Task.Delay(50);
            Ensure(!remove.IsCompleted,
                "same-stripe removal must not pass a generation capture still inside its callback");

            releaseCapture.Set();
            capturedLease = await capture.WaitAsync(RaceCoordinationTimeout);
            Ensure(await remove.WaitAsync(RaceCoordinationTimeout),
                "removal must continue after the projection releases its stripe lock");
            leaseAcquired = capturedLease.TryAcquire();
            Ensure(leaseAcquired,
                "the atomically projected lease must retain the pre-removal generation");
        }
        finally
        {
            releaseCapture.Set();
            await LongRunningTestWorker.JoinAsync(capture, RaceCoordinationTimeout);
            if (remove is not null)
                await LongRunningTestWorker.JoinAsync(remove, RaceCoordinationTimeout);
            if (leaseAcquired)
                capturedLease.ReleaseUse();
            _ = calls.TryRemove(state.RequestId, state);
            state.Dispose();
        }
    }

    [Test]
    public async Task CopyEntriesShouldProjectUnderEachStripeLockWithoutBlockingOtherStripes()
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions
        {
            StripeCount = 2,
            InitialMapCapacityPerStripe = 1
        });
        var capturedState = Rent(
            100, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        var otherStripeState = Rent(
            101, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        calls.Set(capturedState.RequestId, capturedState);
        calls.Set(otherStripeState.RequestId, otherStripeState);
        using var projectionEntered = new ManualResetEventSlim();
        using var releaseProjection = new ManualResetEventSlim();
        using var sameStripeRemoveStarted = new ManualResetEventSlim();
        var leases = new ServerCallCancellationLease[2];
        var copy = LongRunningTestWorker.Run(() => calls.CopyEntries(
            leases,
            (requestId, state) =>
            {
                if (requestId == capturedState.RequestId)
                {
                    projectionEntered.Set();
                    releaseProjection.Wait();
                }
                return state.CaptureLease(requestId);
            }));
        Task<bool>? sameStripeRemove = null;
        Task<bool>? otherStripeRemove = null;
        var capturedLeaseAcquired = false;
        try
        {
            Ensure(projectionEntered.Wait(RaceCoordinationTimeout),
                "CopyEntries must enter the first stripe projection before removal races begin");
            sameStripeRemove = LongRunningTestWorker.Run(() =>
            {
                sameStripeRemoveStarted.Set();
                return calls.TryRemove(capturedState.RequestId, capturedState);
            });
            Ensure(sameStripeRemoveStarted.Wait(RaceCoordinationTimeout),
                "same-stripe removal must reach the locked operation");
            otherStripeRemove = LongRunningTestWorker.Run(
                () => calls.TryRemove(otherStripeState.RequestId, otherStripeState));

            Ensure(await otherStripeRemove.WaitAsync(RaceCoordinationTimeout),
                "a blocked projection must not serialize an independent stripe");
            Ensure(!sameStripeRemove.IsCompleted,
                "same-stripe removal must not pass a CopyEntries projection still using the pooled state");

            releaseProjection.Set();
            Ensure(await copy.WaitAsync(RaceCoordinationTimeout) == 1,
                "the per-stripe snapshot must exclude the independently removed later-stripe entry");
            Ensure(await sameStripeRemove.WaitAsync(RaceCoordinationTimeout),
                "same-stripe removal must continue after its projection releases the stripe lock");
            capturedLeaseAcquired = leases[0].TryAcquire();
            Ensure(capturedLeaseAcquired,
                "the projected lease must retain the generation that was stable under the stripe lock");
        }
        finally
        {
            releaseProjection.Set();
            await LongRunningTestWorker.JoinAsync(copy, RaceCoordinationTimeout);
            if (sameStripeRemove is not null)
                await LongRunningTestWorker.JoinAsync(sameStripeRemove, RaceCoordinationTimeout);
            if (otherStripeRemove is not null)
                await LongRunningTestWorker.JoinAsync(otherStripeRemove, RaceCoordinationTimeout);
            if (capturedLeaseAcquired)
                leases[0].ReleaseUse();
            _ = calls.TryRemove(capturedState.RequestId, capturedState);
            _ = calls.TryRemove(otherStripeState.RequestId, otherStripeState);
            capturedState.Dispose();
            otherStripeState.Dispose();
        }
    }

    [Test]
    // The assertion forces reuse through the process-wide ServerCallCancellationState pool.
    [NotInParallel]
    public void OldSnapshotFromSameMapShouldNotAcquireAReusedSameIdState()
        => AssertOldSnapshotCannotAcquireReusedSameIdState(reuseSameMap: true);

    private static void AssertOldSnapshotCannotAcquireReusedSameIdState(bool reuseSameMap)
    {
        var oldCalls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var newCalls = reuseSameMap
            ? oldCalls
            : new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var first = Rent(
            50, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        oldCalls.Set(first.RequestId, first);
        var snapshot = new ServerCallCancellationLease[1];
        Ensure(oldCalls.CopyEntries(
                   snapshot,
                   static (requestId, state) => state.CaptureLease(requestId)) == 1,
            "snapshot count");
        var wrongIdLease = new ServerCallCancellationLease(
            snapshot[0].State,
            snapshot[0].RequestId + 1,
            snapshot[0].Generation);
        var wrongIdAcquired = wrongIdLease.TryAcquire();
        if (wrongIdAcquired)
            wrongIdLease.ReleaseUse();
        Ensure(!wrongIdAcquired,
            "a generation match must not let a lease acquire the wrong request ID");
        Ensure(oldCalls.TryRemove(first.RequestId, first), "remove old call");
        first.Dispose();

        var reused = Rent(
            50, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        Ensure(ReferenceEquals(first, reused),
            "a rejected wrong-ID lease must not leak an external owner that prevents immediate reuse");
        newCalls.Set(reused.RequestId, reused);
        var staleSnapshotAcquired = snapshot[0].TryAcquire();
        if (staleSnapshotAcquired)
            snapshot[0].ReleaseUse();
        Ensure(!staleSnapshotAcquired,
            "an old snapshot must not acquire a same-ID state after its pooled generation changes");
        Ensure(newCalls.TryCapture(
                   reused.RequestId,
                   static (requestId, state) => state.CaptureLease(requestId),
                   out var currentLease) &&
               currentLease.TryAcquire(),
            "a freshly captured lease must acquire the current same-ID generation");
        currentLease.ReleaseUse();
        Ensure(newCalls.TryRemove(reused.RequestId, reused), "remove new call");
        reused.Dispose();
        var returned = Rent(
            51, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        Ensure(ReferenceEquals(reused, returned),
            "a rejected stale-generation lease must not leak an external owner after final disposal");
        returned.Dispose();
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
                ? RpcDeadline.FromTimestamp(deadlineTimestamp)
                : default,
            TimeProvider.System,
            connectionClosedToken,
            serverStoppingToken,
            moduleDrainingToken,
            supportsCooperativeCancellation);

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
        var response = LongRunningTestWorker.Run(() =>
        {
            ready.Signal();
            start.Wait();
            responseWon = state.TryClaimResponse();
        });
        var cancellation = LongRunningTestWorker.Run(() =>
        {
            ready.Signal();
            start.Wait();
            cancellationWon = state.TryCancel(cancellationReason);
        });
        try
        {
            Ensure(ready.Wait(RaceCoordinationTimeout), $"{scenario}: workers must reach the start gate");
            start.Set();
            Ensure(Task.WaitAll([response, cancellation], RaceCoordinationTimeout),
                $"{scenario}: workers must finish within the race bound");
            return (responseWon, cancellationWon);
        }
        finally
        {
            start.Set();
            LongRunningTestWorker.Join(response, RaceCoordinationTimeout);
            LongRunningTestWorker.Join(cancellation, RaceCoordinationTimeout);
        }
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
        => Schedule(state, TimeProvider.System);

    private static ScheduledCall Schedule(ServerCallCancellationState state, TimeProvider timeProvider)
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        calls.Set(state.RequestId, state);
        var scheduler = new ServerCallDeadlineScheduler(
            calls,
            maxCalls: 1,
            timeProvider);
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
