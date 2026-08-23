using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallCancellationDeadlinePriorityTests
{
    [Test]
    public void ExpiredDeadlineShouldBeatConnectionCloseWithoutTimerCallback()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            91001,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        try
        {
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

            Ensure(state.TryCancel(ServerCallCancellationReason.ConnectionClosed),
                "the first terminal claim should still succeed");
            Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                "an already-expired monotonic deadline must outrank a later connection close");
            Ensure(state.InvocationToken.IsCancellationRequested,
                "deadline promotion must still cancel cooperative business code");
        }
        finally
        {
            state.Dispose();
        }
    }

    [Test]
    public void CancellationBeforeDeadlineShouldRemainTheWinner()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            91002,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        try
        {
            Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel),
                "caller cancellation before the boundary should claim the call");
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

            Ensure(state.Reason == ServerCallCancellationReason.RemoteCancel,
                "a deadline that expires after cancellation must not replace the earlier winner");
        }
        finally
        {
            state.Dispose();
        }
    }
}
