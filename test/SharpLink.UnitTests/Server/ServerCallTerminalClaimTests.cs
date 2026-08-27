using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallTerminalClaimTests
{
    [Test]
    public void ExpiredDeadlineShouldWinLaterInfrastructureTerminalClaimWithoutTimerCallback()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            9101,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        try
        {
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

            Ensure(state.TryCancel(ServerCallCancellationReason.ConnectionClosed),
                "the infrastructure terminal contender should claim the still-unclaimed call");
            Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                "the shared terminal claim must promote an already-expired deadline");
            Ensure(state.InvocationToken.IsCancellationRequested,
                "deadline promotion should cancel cooperative business work");
        }
        finally
        {
            state.Dispose();
        }
    }

    [Test]
    public void ExpiredDeadlineShouldRejectResponseThroughTheSameTerminalClaim()
    {
        var timeProvider = new ManualTimeProvider();
        var state = ServerCallCancellationState.Rent(
            9102,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        try
        {
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

            Ensure(!state.TryClaimResponse(),
                "a response cannot claim completion after the monotonic deadline");
            Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                "response and cancellation contenders must share the same deadline arbitration");
        }
        finally
        {
            state.Dispose();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
