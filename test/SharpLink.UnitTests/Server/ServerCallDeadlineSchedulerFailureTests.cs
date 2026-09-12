using System.Buffers;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallDeadlineSchedulerFailureTests
{
    [Test]
    public void DisposeDuringScanShouldLeaveTimerDisarmed()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new FailureTrackingLeasePool();
        var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 8, timeProvider, pool);
        var state = CreateState(1, TimeSpan.FromSeconds(1), timeProvider);
        calls.Set(state.RequestId, state);
        scheduler.Register(state);
        pool.OnRent = scheduler.Dispose;

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(timeProvider.ActiveTimerCount == 0,
            "disposing during a scan must prevent the callback from re-arming its timer");
        Ensure(pool.RentCount == 1, "the in-flight scan should complete at most one rent");
        Ensure(!pool.SawLiveLeaseOnReturn,
            "disposing during a scan must not leave a captured lease in the returned array");

        _ = calls.TryRemove(state.RequestId, state);
        state.Dispose();
    }

    [Test]
    public void ReturnExceptionShouldStillRearmFutureDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new FailureTrackingLeasePool { ThrowOnNextReturn = true };
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 8, timeProvider, pool);
        var first = CreateState(10, TimeSpan.FromSeconds(1), timeProvider);
        var second = CreateState(11, TimeSpan.FromSeconds(2), timeProvider);
        calls.Set(first.RequestId, first);
        calls.Set(second.RequestId, second);
        scheduler.Register(first);
        scheduler.Register(second);

        try
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            throw new Exception("the injected pool return failure should escape the deterministic timer callback");
        }
        catch (InvalidOperationException exception) when (exception.Message == "injected return failure")
        {
        }

        Ensure(first.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the expired entry must be processed before the injected return failure");
        Ensure(second.Reason == ServerCallCancellationReason.None,
            "the later deadline must remain live after the failed scan cleanup");
        Ensure(timeProvider.ActiveTimerCount == 1,
            "the outer scan finally must re-arm the later deadline even when pool return throws");

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(second.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the re-armed timer must process the future deadline after the prior scan exception");
        Ensure(!pool.SawLiveLeaseOnReturn,
            "partial/failing cleanup must not return a snapshot retaining a live lease");

        Cleanup(calls, first);
        Cleanup(calls, second);
    }

    [Test]
    [NotInParallel]
    public void OneHundredThousandScansShouldReturnEveryStateToItsPool()
    {
        const int iterations = 100_000;
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new FailureTrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 1, timeProvider, pool);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var state = CreateState(iteration + 100, TimeSpan.FromTicks(1), timeProvider);
            calls.Set(state.RequestId, state);
            scheduler.Register(state);

            timeProvider.Advance(TimeSpan.FromTicks(1));

            Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                "every repeated scan must still acquire and release its generation-bound lease");
            Ensure(calls.TryRemove(state.RequestId, state), "repeated scan cleanup");
            state.Dispose();

            var reused = CreateState(1_000_000L + iteration, TimeSpan.FromSeconds(1), timeProvider);
            Ensure(ReferenceEquals(state, reused),
                "disposed scan state must be immediately reusable, proving no lease use count remains");
            reused.Dispose();
        }

        Ensure(pool.RentCount == iterations,
            "each isolated exact-deadline scan should perform one bounded snapshot rent");
        Ensure(!pool.SawLiveLeaseOnReturn,
            "100k repeated scans must not return any snapshot retaining a live lease reference");
    }

    private static ServerCallCancellationState CreateState(
        long requestId,
        TimeSpan deadlineAfter,
        ManualTimeProvider timeProvider)
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

    private sealed class FailureTrackingLeasePool : ArrayPool<ServerCallCancellationLease>
    {
        internal Action? OnRent { get; set; }
        internal bool ThrowOnNextReturn { get; set; }
        internal bool SawLiveLeaseOnReturn { get; private set; }
        internal int RentCount { get; private set; }

        public override ServerCallCancellationLease[] Rent(int minimumLength)
        {
            RentCount++;
            OnRent?.Invoke();
            return new ServerCallCancellationLease[minimumLength];
        }

        public override void Return(ServerCallCancellationLease[] array, bool clearArray = false)
        {
            for (var index = 0; index < array.Length; index++)
            {
                if (!array[index].TryAcquire())
                    continue;
                SawLiveLeaseOnReturn = true;
                array[index].ReleaseUse();
            }

            if (!ThrowOnNextReturn)
                return;

            ThrowOnNextReturn = false;
            throw new InvalidOperationException("injected return failure");
        }
    }
}
