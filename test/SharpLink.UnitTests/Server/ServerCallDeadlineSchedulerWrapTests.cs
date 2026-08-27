using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallDeadlineSchedulerWrapTests
{
    [Test]
    public void DeadlineSchedulerShouldPreserveOrderAcrossSignedTimestampBoundary()
    {
        var start = long.MaxValue - TimeSpan.FromMilliseconds(500).Ticks;
        var timeProvider = new WrappingManualTimeProvider(start);
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 8, timeProvider);
        var first = CreateState(1, TimeSpan.FromMilliseconds(250), timeProvider);
        var later = CreateState(2, TimeSpan.FromSeconds(1), timeProvider);

        calls.Set(first.RequestId, first);
        calls.Set(later.RequestId, later);
        scheduler.Register(first);
        scheduler.Register(later);

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        Ensure(first.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the pre-wrap server deadline must expire first");
        Ensure(later.Reason == ServerCallCancellationReason.None,
            "the later cross-boundary deadline must not be misordered ahead of the earlier deadline");

        timeProvider.Advance(TimeSpan.FromMilliseconds(750));
        Ensure(later.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the later server deadline must expire at its own modular boundary");

        Cleanup(calls, first);
        Cleanup(calls, later);
    }

    private static ServerCallCancellationState CreateState(
        long requestId,
        TimeSpan deadlineAfter,
        TimeProvider timeProvider)
        => ServerCallCancellationState.Rent(
            requestId,
            RpcDeadline.Create(deadlineAfter, timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);

    private static void Cleanup(
        StripedLongMap<ServerCallCancellationState> calls,
        ServerCallCancellationState state)
    {
        _ = calls.TryRemove(state.RequestId, state);
        state.Dispose();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
