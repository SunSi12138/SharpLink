using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerCallCancellationStateTests
{
    [Test]
    public void FirstCancellationSourceShouldWin()
    {
        using var connectionClosed = new CancellationTokenSource();
        using var serverStopping = new CancellationTokenSource();
        var state = ServerCallCancellationState.Rent(
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
        var state = ServerCallCancellationState.Rent(
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
        var state = ServerCallCancellationState.Rent(
            20,
            DateTimeOffset.UtcNow.AddMilliseconds(25),
            DeadlineAfter(TimeSpan.FromMilliseconds(25)),
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        using var scheduledCall = Schedule(state);
        var observedReason = ServerCallCancellationReason.None;
        using var registration = state.InvocationToken.Register(
            () => observedReason = state.Reason);

        await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded);

        Ensure(observedReason == ServerCallCancellationReason.DeadlineExceeded,
            "business cancellation callbacks must observe the published deadline reason");
    }

    [Test]
    public async Task NonCooperativeDeadlineShouldNotCreateInvocationCancellationSource()
    {
        var state = ServerCallCancellationState.Rent(
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
    public async Task UserCancellationBeforeDeadlineShouldRemainTheTerminalReason()
    {
        var state = ServerCallCancellationState.Rent(
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
        var state = ServerCallCancellationState.Rent(
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
        var state = ServerCallCancellationState.Rent(
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
        var serverState = ServerCallCancellationState.Rent(
            3, null, 0, firstConnection.Token, firstServer.Token, supportsCooperativeCancellation: true);
        firstServer.Cancel();
        firstConnection.Cancel();
        Ensure(serverState.Reason == ServerCallCancellationReason.ServerStopping, "server stop reason");

        using var secondConnection = new CancellationTokenSource();
        using var secondServer = new CancellationTokenSource();
        var connectionState = ServerCallCancellationState.Rent(
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
            var state = ServerCallCancellationState.Rent(
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
        var state = ServerCallCancellationState.Rent(
            5, null, 0, connectionClosed.Token, CancellationToken.None, supportsCooperativeCancellation: true);

        Ensure(state.TryClaimResponse(), "normal completion should claim the terminal state");
        connectionClosed.Cancel();

        Ensure(state.Reason == ServerCallCancellationReason.Completed, "completed state must be stable");
        state.Dispose();
    }

    [Test]
    public void ThrowingUserCancellationCallbackShouldNotEscapeFrameworkCancellation()
    {
        var state = ServerCallCancellationState.Rent(
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
            var state = ServerCallCancellationState.Rent(
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
    public void OldSnapshotShouldNotAcquireAReusedPooledState()
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var first = ServerCallCancellationState.Rent(
            50, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        calls.Set(first.RequestId, first);
        var snapshot = new KeyValuePair<long, ServerCallCancellationState>[1];
        Ensure(calls.CopyEntries(snapshot) == 1, "snapshot count");
        Ensure(calls.TryRemove(first.RequestId, first), "remove old call");
        first.Dispose();

        var reused = ServerCallCancellationState.Rent(
            51, null, 0, CancellationToken.None, CancellationToken.None,
            supportsCooperativeCancellation: false);
        Ensure(ReferenceEquals(snapshot[0].Value, reused),
            "the test must exercise the same pooled state instance");
        Ensure(!snapshot[0].Value.TryAcquire(snapshot[0].Key),
            "an old request ID must not acquire a new lease");
        reused.Dispose();
    }

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

    private static ScheduledCall Schedule(ServerCallCancellationState state)
    {
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        calls.Set(state.RequestId, state);
        var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 1);
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
