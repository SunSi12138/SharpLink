using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallCancellationDeadlinePriorityTests
{
    [Test]
    public async Task ExpiredDeadlineShouldBeatConnectionCloseWithoutTimerCallback()
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

            await Assert.That(state.TryCancel(ServerCallCancellationReason.ConnectionClosed)).IsTrue();
            await Assert.That(state.Reason).IsEqualTo(ServerCallCancellationReason.DeadlineExceeded);
            await Assert.That(state.InvocationToken.IsCancellationRequested).IsTrue();
        }
        finally
        {
            state.Dispose();
        }
    }

    [Test]
    public async Task CancellationBeforeDeadlineShouldRemainTheWinner()
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
            await Assert.That(state.TryCancel(ServerCallCancellationReason.RemoteCancel)).IsTrue();
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

            await Assert.That(state.Reason).IsEqualTo(ServerCallCancellationReason.RemoteCancel);
        }
        finally
        {
            state.Dispose();
        }
    }
}
