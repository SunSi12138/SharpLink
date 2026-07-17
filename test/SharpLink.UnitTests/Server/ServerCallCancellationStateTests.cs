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
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);

        await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded);

        Ensure(state.InvocationToken.IsCancellationRequested, "deadline should cancel the invocation token");
        Ensure(!state.TryClaimResponse(), "deadline must suppress a late response");
        state.Dispose();
    }

    [Test]
    public void ServerStopAndConnectionCloseShouldHaveDistinctReasons()
    {
        using var firstConnection = new CancellationTokenSource();
        using var firstServer = new CancellationTokenSource();
        var serverState = ServerCallCancellationState.Rent(
            3, null, firstConnection.Token, firstServer.Token, supportsCooperativeCancellation: true);
        firstServer.Cancel();
        firstConnection.Cancel();
        Ensure(serverState.Reason == ServerCallCancellationReason.ServerStopping, "server stop reason");

        using var secondConnection = new CancellationTokenSource();
        using var secondServer = new CancellationTokenSource();
        var connectionState = ServerCallCancellationState.Rent(
            4, null, secondConnection.Token, secondServer.Token, supportsCooperativeCancellation: true);
        secondConnection.Cancel();
        secondServer.Cancel();
        Ensure(connectionState.Reason == ServerCallCancellationReason.ConnectionClosed, "connection close reason");

        serverState.Dispose();
        connectionState.Dispose();
    }

    [Test]
    public void CompletedCallShouldIgnoreLaterCancellation()
    {
        using var connectionClosed = new CancellationTokenSource();
        var state = ServerCallCancellationState.Rent(
            5, null, connectionClosed.Token, CancellationToken.None, supportsCooperativeCancellation: true);

        Ensure(state.TryClaimResponse(), "normal completion should claim the terminal state");
        connectionClosed.Cancel();

        Ensure(state.Reason == ServerCallCancellationReason.Completed, "completed state must be stable");
        state.Dispose();
    }

    [Test]
    public void ThrowingUserCancellationCallbackShouldNotEscapeFrameworkCancellation()
    {
        var state = ServerCallCancellationState.Rent(
            6, null, CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);
        using var registration = state.InvocationToken.Register(static () => throw new InvalidOperationException("user callback"));

        Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "framework cancellation should still succeed");
        Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel, "cancellation reason should remain observable");
        state.Dispose();
    }

    [Test]
    public void CancelCompletionAndDisposeRaceShouldNotCorruptPooledState()
    {
        for (var iteration = 1; iteration <= 10_000; iteration++)
        {
            var requestId = iteration;
            var state = ServerCallCancellationState.Rent(
                requestId,
                null,
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
                    state.Dispose();
                });
        }
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
