using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallDeadlineSchedulerSizingTests
{
    [Test]
    public void LargeMaximumWithOneActiveCallShouldRentForObservedOccupancyAndNeverExpireEarly()
    {
        const int maxCalls = 65_536;
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new TrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls, timeProvider, pool);
        var state = CreateState(1, TimeSpan.FromSeconds(1), timeProvider);
        calls.Set(state.RequestId, state);
        scheduler.Register(state);

        timeProvider.Advance(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        Ensure(state.Reason == ServerCallCancellationReason.None,
            "deadline must not expire one provider tick early");
        Ensure(pool.RentCount == 0, "the timer must not scan before the exact deadline");

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Ensure(state.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the exact deadline must expire the call");
        Ensure(pool.RentCount == 1, "one active call should need one snapshot rent");
        Ensure(pool.RequestedLengths[0] == 16,
            "one active call should use the minimum snapshot bucket, not maxCalls");
        Ensure(pool.RequestedLengths[0] < maxCalls,
            "high maxCalls must not become the per-scan temporary size");
        Ensure(pool.ClearArrayRequests == 0,
            "scheduler should clear only written lease slots before returning the array");
        Ensure(!pool.SawLiveLeaseOnReturn,
            "returned snapshot must not retain captured call-state references");

        Ensure(calls.TryRemove(state.RequestId, state), "cleanup active call");
        state.Dispose();
    }

    [Test]
    public void RemovedLastCallShouldLetArmedTimerScanWithoutRentingSnapshot()
    {
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new TrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 65_536, timeProvider, pool);
        var state = CreateState(2, TimeSpan.FromSeconds(1), timeProvider);
        calls.Set(state.RequestId, state);
        scheduler.Register(state);
        Ensure(calls.TryRemove(state.RequestId, state), "remove before the armed deadline");
        state.Dispose();

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(pool.RentCount == 0,
            "an armed timer with zero active calls must not rent a temporary snapshot");
    }

    [Test]
    public void RegistrationBetweenCountHintAndCopyShouldGrowAndRetryWithoutDroppingDeadlines()
    {
        const int initialCalls = 16;
        const int racedCalls = 16;
        const int maxCalls = 64;
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new TrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls, timeProvider, pool);
        var states = new List<ServerCallCancellationState>();

        for (var index = 0; index < initialCalls; index++)
        {
            var state = CreateState(100 + index, TimeSpan.FromSeconds(1), timeProvider);
            states.Add(state);
            calls.Set(state.RequestId, state);
            scheduler.Register(state);
        }

        pool.OnRent = rentCount =>
        {
            if (rentCount != 1)
                return;
            for (var index = 0; index < racedCalls; index++)
            {
                var state = CreateState(1_000 + index, TimeSpan.FromSeconds(2), timeProvider);
                states.Add(state);
                calls.Set(state.RequestId, state);
                scheduler.Register(state);
            }
        };

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(pool.RentCount == 2,
            "a deterministic count/copy race should require exactly one bounded growth retry");
        Ensure(pool.RequestedLengths[0] == 24 && pool.RequestedLengths[1] == 48,
            "retry should grow geometrically instead of jumping directly to maxCalls");
        for (var index = 0; index < initialCalls; index++)
        {
            Ensure(states[index].Reason == ServerCallCancellationReason.DeadlineExceeded,
                "all deadlines present before the race must be scanned");
        }
        for (var index = initialCalls; index < states.Count; index++)
        {
            Ensure(states[index].Reason == ServerCallCancellationReason.None,
                "future calls added during sizing must remain live before their exact deadline");
        }

        pool.OnRent = null;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        for (var index = initialCalls; index < states.Count; index++)
        {
            Ensure(states[index].Reason == ServerCallCancellationReason.DeadlineExceeded,
                "calls added during the sizing race must not be lost by the retry");
        }

        Cleanup(calls, states);
    }

    [Test]
    public void RemovalAfterCountHintShouldOnlyOverRentAndStillScanRemainingCall()
    {
        const int activeCalls = 32;
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new TrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls: 64, timeProvider, pool);
        var states = new List<ServerCallCancellationState>();

        for (var index = 0; index < activeCalls; index++)
        {
            var state = CreateState(2_000 + index, TimeSpan.FromSeconds(1), timeProvider);
            states.Add(state);
            calls.Set(state.RequestId, state);
            scheduler.Register(state);
        }

        pool.OnRent = rentCount =>
        {
            if (rentCount != 1)
                return;
            for (var index = 1; index < states.Count; index++)
                Ensure(calls.TryRemove(states[index].RequestId, states[index]), "race removal");
        };

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(pool.RentCount == 1,
            "a stale-high count hint should waste capacity only, not trigger a retry");
        Ensure(pool.RequestedLengths[0] == 40,
            "the first rent should reflect the pre-removal count hint plus headroom");
        Ensure(states[0].Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the remaining active call must still be scanned");
        for (var index = 1; index < states.Count; index++)
        {
            Ensure(states[index].Reason == ServerCallCancellationReason.None,
                "removed calls must not be canceled by a snapshot taken after removal");
        }

        Cleanup(calls, states);
    }

    [Test]
    public void RepeatedRegistrationRacesShouldHaveBoundedGeometricGrowthBeforeMaximumFallback()
    {
        const int maxCalls = 64;
        var timeProvider = new ManualTimeProvider();
        var calls = new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions());
        var pool = new TrackingLeasePool();
        using var scheduler = new ServerCallDeadlineScheduler(calls, maxCalls, timeProvider, pool);
        var states = new List<ServerCallCancellationState>();
        var initial = CreateState(3_000, TimeSpan.FromSeconds(1), timeProvider);
        states.Add(initial);
        calls.Set(initial.RequestId, initial);
        scheduler.Register(initial);
        var nextRequestId = 3_001L;

        pool.OnRent = rentCount =>
        {
            if (rentCount > 2)
                return;
            var targetCount = rentCount == 1 ? 17 : 33;
            while (calls.Count < targetCount)
            {
                var state = CreateState(nextRequestId++, TimeSpan.FromSeconds(2), timeProvider);
                states.Add(state);
                calls.Set(state.RequestId, state);
                scheduler.Register(state);
            }
        };

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Ensure(pool.RentCount == 3,
            "continued growth races must remain bounded and converge at the configured maximum");
        Ensure(pool.RequestedLengths[0] == 16 &&
               pool.RequestedLengths[1] == 32 &&
               pool.RequestedLengths[2] == 64,
            "snapshot growth must be geometric and capped by maxCalls");
        Ensure(initial.Reason == ServerCallCancellationReason.DeadlineExceeded,
            "the original deadline must still be processed after bounded retries");
        Ensure(pool.RequestedLengths.TrueForAll(static length => length <= maxCalls),
            "no retry may request a snapshot beyond the configured admission bound");

        pool.OnRent = null;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        for (var index = 1; index < states.Count; index++)
        {
            Ensure(states[index].Reason == ServerCallCancellationReason.DeadlineExceeded,
                "registrations that forced retries must be scanned at their deadline");
        }

        Cleanup(calls, states);
    }

    private static ServerCallCancellationState CreateState(
        long requestId,
        TimeSpan deadlineAfter,
        ManualTimeProvider timeProvider)
        => ServerCallCancellationState.Rent(
            requestId,
            RpcDeadline.Create(timeProvider.GetUtcNow().Add(deadlineAfter), timeProvider),
            timeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);

    private static void Cleanup(
        StripedLongMap<ServerCallCancellationState> calls,
        List<ServerCallCancellationState> states)
    {
        foreach (var state in states)
        {
            _ = calls.TryRemove(state.RequestId, state);
            state.Dispose();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TrackingLeasePool : ArrayPool<ServerCallCancellationLease>
    {
        internal List<int> RequestedLengths { get; } = [];
        internal Action<int>? OnRent { get; set; }
        internal int ClearArrayRequests { get; private set; }
        internal bool SawLiveLeaseOnReturn { get; private set; }
        internal int RentCount => RequestedLengths.Count;

        public override ServerCallCancellationLease[] Rent(int minimumLength)
        {
            RequestedLengths.Add(minimumLength);
            OnRent?.Invoke(RequestedLengths.Count);
            return new ServerCallCancellationLease[minimumLength];
        }

        public override void Return(ServerCallCancellationLease[] array, bool clearArray = false)
        {
            if (clearArray)
                ClearArrayRequests++;
            for (var index = 0; index < array.Length; index++)
            {
                if (!array[index].TryAcquire())
                    continue;
                SawLiveLeaseOnReturn = true;
                array[index].ReleaseUse();
            }
        }
    }
}
