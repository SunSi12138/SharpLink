using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowCleanSemanticsTests
{
    [Test]
    public void LimitOnlySuccessorSharesCounterAndPreservesProgramSnapshot()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(3, TimeSpan.FromSeconds(30), time);

        Ensure(Acquire(source), "source permit one must succeed");
        Ensure(Acquire(source), "source permit two must succeed");

        using var target = CreateFixed(1, TimeSpan.FromSeconds(30), time, source);
        source.CommitTransitionTo(target);

        Ensure(source.FixedWindowForTests!.CounterIdentityForTests ==
               target.FixedWindowForTests!.CounterIdentityForTests,
            "Fixed->Fixed successors must share one accounting counter");
        Ensure(target.FixedWindowForTests.ConsumedForTests == 2,
            "publication must not reset already consumed quota");
        Ensure(!Acquire(target), "newly captured policy must observe the immediate shrink");
        Ensure(Acquire(source), "an already captured source policy keeps its immutable limit view");
        Ensure(target.FixedWindowForTests.ConsumedForTests == 3,
            "old and new views must charge the exact same counter");
        Ensure(!Acquire(target), "the new policy must remain blocked after the old captured grant");
    }

    [Test]
    public void LimitIncreaseExposesOnlyTheDifferenceWithoutCreatingAnotherLedger()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(2, TimeSpan.FromSeconds(30), time);
        Ensure(Acquire(source), "source permit one must succeed");
        Ensure(Acquire(source), "source permit two must succeed");

        using var target = CreateFixed(3, TimeSpan.FromSeconds(30), time, source);
        source.CommitTransitionTo(target);

        Ensure(Acquire(target), "limit two to three may expose exactly one additional permit");
        Ensure(!Acquire(target), "limit increase must not expose a fresh three-permit window");
        Ensure(target.FixedWindowForTests!.ConsumedForTests == 3,
            "all three grants must be recorded by one counter");
    }

    [Test]
    public void WindowChangeActivatesAtTheNextOldNaturalBoundary()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(4, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "source permit one must succeed");
        Ensure(Acquire(source), "source permit two must succeed");
        Ensure(Acquire(source), "source permit three must succeed");

        time.Advance(TimeSpan.FromSeconds(3));
        using var target = CreateFixed(5, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(target);

        Ensure(target.FixedWindowForTests!.HasPendingWindowForTests,
            "changed Window must be pending instead of re-anchoring the current window");
        Ensure(target.FixedWindowForTests.ActiveWindowForTests == TimeSpan.FromSeconds(10),
            "the old natural window must remain active until its boundary");
        Ensure(Acquire(target), "before the boundary the target view keeps the source limit");
        Ensure(!Acquire(target), "the current old window must remain exhausted");

        time.Advance(TimeSpan.FromSeconds(7).Subtract(TimeSpan.FromTicks(1)));
        Ensure(!Acquire(target), "changed Window must not activate one tick before the old boundary");
        time.Advance(TimeSpan.FromTicks(1));

        Ensure(target.FixedWindowForTests.ActiveWindowForTests == TimeSpan.FromSeconds(20),
            "the target Window must activate exactly at the old natural boundary");
        for (var index = 0; index < 5; index++)
            Ensure(Acquire(target), $"new-window permit {index + 1} must succeed");
        Ensure(!Acquire(target), "the new active limit must be enforced");
    }

    [Test]
    public void OldPolicyViewsConvergeWhenAChangedWindowActuallyActivates()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(2, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "old window permit one must succeed");
        Ensure(Acquire(source), "old window permit two must succeed");

        using var target = CreateFixed(5, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(target);
        time.Advance(TimeSpan.FromSeconds(10));

        for (var index = 0; index < 5; index++)
            Ensure(Acquire(source), $"old view must converge to the active target window at grant {index + 1}");
        Ensure(!Acquire(source),
            "after Window activation an old view must not maintain a parallel old-window ledger");
    }

    [Test]
    public void LatestWinningWindowTargetReplacesEarlierPendingTarget()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(1, TimeSpan.FromSeconds(30), time);
        Ensure(Acquire(source), "source window must be exhausted");

        using var first = CreateFixed(2, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(first);
        using var second = CreateFixed(4, TimeSpan.FromSeconds(10), time, first);
        first.CommitTransitionTo(second);

        time.Advance(TimeSpan.FromSeconds(30));
        Ensure(second.FixedWindowForTests!.ActiveWindowForTests == TimeSpan.FromSeconds(10),
            "last winning pending Window must activate");
        Ensure(second.FixedWindowForTests.ActiveLimitForTests == 4,
            "last winning pending limit must activate with its Window");
        for (var index = 0; index < 4; index++)
            Ensure(Acquire(second), $"winning target permit {index + 1} must succeed");
        Ensure(!Acquire(second), "winning target limit must be enforced");
    }

    [Test]
    public void LosingCandidateConstructionCannotMutateTheLiveCounter()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(1, TimeSpan.FromSeconds(30), time);
        Ensure(Acquire(source), "source window must be exhausted");

        using (var losing = CreateFixed(5, TimeSpan.FromSeconds(30), time, source))
        {
            Ensure(source.FixedWindowForTests!.CounterIdentityForTests ==
                   losing.FixedWindowForTests!.CounterIdentityForTests,
                "candidate preparation may share lifecycle state without committing a target");
            Ensure(!Acquire(source), "uncommitted candidate must not expose its larger limit to live state");
        }

        Ensure(!Acquire(source), "disposing a losing candidate must leave live accounting unchanged");
    }

    [Test]
    public async Task QueuedSourceViewRemainsSnapshotBoundAcrossLimitIncrease()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(1, TimeSpan.FromSeconds(20), time);
        Ensure(Acquire(source), "source window must be exhausted");

        var queued = source.AcquireAsync(1, CancellationToken.None).AsTask();
        Ensure(!queued.IsCompleted && source.FixedWindowForTests!.WaitingCount == 1,
            "source waiter must queue on the shared counter");

        using var target = CreateFixed(3, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(target);
        Ensure(!queued.IsCompleted,
            "an already queued source RPC keeps its captured policy rather than being silently rebound");

        time.Advance(TimeSpan.FromSeconds(20));
        using var lease = await queued;
        Ensure(lease.IsAcquired, "source waiter must complete at its natural boundary");
        Ensure(target.FixedWindowForTests!.ConsumedForTests == 1,
            "queued completion must charge the same shared counter exactly once");
    }

    private static AdmissionRateState CreateFixed(
        int permitLimit,
        TimeSpan window,
        TimeProvider timeProvider,
        AdmissionRateState? source = null)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(options =>
        {
            options.PermitLimit = permitLimit;
            options.Window = window;
        });
        return AdmissionRateState.Create(rule, timeProvider, source);
    }

    private static bool Acquire(RateLimiter limiter)
    {
        using var lease = limiter.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            List<ManualTimer> due;
            lock (_gate)
            {
                _timestamp = checked(_timestamp + delta.Ticks);
                due = [];
                foreach (var timer in _timers)
                    if (timer.TakeIfDueLocked(_timestamp))
                        due.Add(timer);
            }

            foreach (var timer in due)
                timer.Invoke();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer.IsDisposed)
                    return false;
                timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_timestamp + Math.Max(0, dueTime.Ticks));
                timer.PeriodTicks = period == Timeout.InfiniteTimeSpan
                    ? 0
                    : Math.Max(1, period.Ticks);
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.IsDisposed = true;
                timer.DueTimestamp = long.MaxValue;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            internal long DueTimestamp = long.MaxValue;
            internal long PeriodTicks;
            internal bool IsDisposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.Change(this, dueTime, period);

            internal bool TakeIfDueLocked(long now)
            {
                if (IsDisposed || DueTimestamp > now)
                    return false;
                DueTimestamp = PeriodTicks == 0
                    ? long.MaxValue
                    : checked(DueTimestamp + PeriodTicks);
                return true;
            }

            internal void Invoke() => callback(state);

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
