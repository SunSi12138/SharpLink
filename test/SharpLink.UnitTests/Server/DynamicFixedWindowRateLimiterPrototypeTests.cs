using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowRateLimiterPrototypeTests
{
    [Test]
    public void ImmediateShrinkPreservesAlreadyChargedConsumption()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(3, TimeSpan.FromSeconds(60), time);

        Ensure(Acquire(limiter), "first permit must be acquired");
        Ensure(Acquire(limiter), "second permit must be acquired");

        limiter.Update(1, TimeSpan.FromSeconds(60), DynamicFixedWindowActivationMode.Immediate);

        Ensure(limiter.Consumed == 2, "immediate shrink must preserve consumed quota");
        Ensure(!Acquire(limiter), "shrink below consumed count must block new grants");

        time.Advance(TimeSpan.FromSeconds(60));
        Ensure(Acquire(limiter), "natural rollover must restore the new limit");
        Ensure(!Acquire(limiter), "new limit must remain one permit per window");
    }

    [Test]
    public void ImmediateWindowChangeStartsNewPolicyEpochWithoutForgivingConsumption()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(3, TimeSpan.FromSeconds(60), time);

        Ensure(Acquire(limiter), "first permit must be acquired");
        Ensure(Acquire(limiter), "second permit must be acquired");
        time.Advance(TimeSpan.FromSeconds(30));

        limiter.Update(3, TimeSpan.FromSeconds(10), DynamicFixedWindowActivationMode.Immediate);

        Ensure(limiter.Consumed == 2, "window change must carry already charged consumption");
        Ensure(Acquire(limiter), "only the remaining capacity may be exposed after update");
        Ensure(!Acquire(limiter), "update must not expose a fresh full window");

        time.Advance(TimeSpan.FromSeconds(9));
        Ensure(!Acquire(limiter), "new window must not roll early");
        time.Advance(TimeSpan.FromSeconds(1));
        Ensure(Acquire(limiter), "new duration must become authoritative after its new epoch boundary");
    }

    [Test]
    public void RepeatedImmediateWindowChangesNeverForgiveConsumption()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(2, TimeSpan.FromSeconds(60), time);

        Ensure(Acquire(limiter), "first permit must be acquired");
        Ensure(Acquire(limiter), "second permit must be acquired");

        for (var index = 0; index < 32; index++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            var window = (index & 1) == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(20);
            limiter.Update(2, window, DynamicFixedWindowActivationMode.Immediate);
            Ensure(limiter.Consumed == 2, $"iteration {index}: consumption must survive immediate update");
            Ensure(!Acquire(limiter), $"iteration {index}: repeated update must not mint a permit");
        }
    }

    [Test]
    public void NextBoundaryKeepsCurrentWindowEntirelyOnOldDefinition()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(2, TimeSpan.FromSeconds(20), time);

        Ensure(Acquire(limiter), "first old-window permit must be acquired");
        Ensure(Acquire(limiter), "second old-window permit must be acquired");
        time.Advance(TimeSpan.FromSeconds(5));

        limiter.Update(5, TimeSpan.FromSeconds(10), DynamicFixedWindowActivationMode.NextWindowBoundary);

        Ensure(limiter.CurrentPermitLimit == 2, "pending update must not mutate current limit");
        Ensure(limiter.CurrentWindow == TimeSpan.FromSeconds(20), "pending update must not mutate current duration");
        Ensure(limiter.HasPendingUpdate, "next-boundary update must be recorded as pending");
        Ensure(!Acquire(limiter), "current old window must remain exhausted");

        time.Advance(TimeSpan.FromSeconds(15));
        Ensure(Acquire(limiter), "pending definition must activate at the old natural boundary");
        Ensure(limiter.CurrentPermitLimit == 5, "new limit must be active after rollover");
        Ensure(limiter.CurrentWindow == TimeSpan.FromSeconds(10), "new duration must be active after rollover");
        Ensure(!limiter.HasPendingUpdate, "pending definition must clear exactly once");
    }

    [Test]
    public void NextBoundaryLatestWinningPendingDefinitionWins()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(1, TimeSpan.FromSeconds(30), time);

        Ensure(Acquire(limiter), "old window must be exhausted");
        limiter.Update(2, TimeSpan.FromSeconds(20), DynamicFixedWindowActivationMode.NextWindowBoundary);
        time.Advance(TimeSpan.FromSeconds(5));
        limiter.Update(4, TimeSpan.FromSeconds(10), DynamicFixedWindowActivationMode.NextWindowBoundary);

        Ensure(limiter.PendingPermitLimit == 4, "latest pending limit must replace earlier pending update");
        Ensure(limiter.PendingWindow == TimeSpan.FromSeconds(10), "latest pending window must replace earlier pending update");

        time.Advance(TimeSpan.FromSeconds(25));
        Ensure(limiter.CurrentPermitLimit == 4, "latest pending definition must activate");
        Ensure(limiter.CurrentWindow == TimeSpan.FromSeconds(10), "latest pending duration must activate");
        for (var index = 0; index < 4; index++)
            Ensure(Acquire(limiter), $"new window permit {index} must be available");
        Ensure(!Acquire(limiter), "new window must enforce the latest pending limit");
    }

    [Test]
    public async Task ImmediateIncreaseCanGrantExistingWaiterWithoutCreatingSecondLedger()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(1, TimeSpan.FromSeconds(60), time);

        Ensure(Acquire(limiter), "first permit must exhaust the window");
        var pending = limiter.AcquireAsync(1, CancellationToken.None).AsTask();
        Ensure(!pending.IsCompleted && limiter.WaitingCount == 1, "second request must queue on the same ledger");

        limiter.Update(2, TimeSpan.FromSeconds(60), DynamicFixedWindowActivationMode.Immediate);

        var lease = await pending;
        Ensure(lease.IsAcquired, "immediate increase must grant the queued waiter from new capacity");
        Ensure(limiter.Consumed == 2, "queued grant must charge the same authoritative ledger");
        Ensure(limiter.WaitingCount == 0, "waiter must drain exactly once");
        lease.Dispose();
    }

    [Test]
    public async Task NextBoundaryQueuedWaiterUsesDefinitionActivatedAtRollover()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(1, TimeSpan.FromSeconds(20), time);

        Ensure(Acquire(limiter), "old window must be exhausted");
        var pending = limiter.AcquireAsync(1, CancellationToken.None).AsTask();
        limiter.Update(2, TimeSpan.FromSeconds(10), DynamicFixedWindowActivationMode.NextWindowBoundary);
        Ensure(!pending.IsCompleted, "queued waiter must stay queued until the old boundary");

        time.Advance(TimeSpan.FromSeconds(20));
        var lease = await pending;
        Ensure(lease.IsAcquired, "waiter must be granted after boundary activation");
        Ensure(limiter.CurrentPermitLimit == 2 && limiter.Consumed == 1,
            "waiter must consume the newly active window, not a hidden old ledger");
        Ensure(limiter.WaitingCount == 0, "waiter must drain exactly once");
        lease.Dispose();
    }

    [Test]
    public async Task QueuedCancellationRemainsExactAcrossPendingUpdate()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(1, TimeSpan.FromSeconds(20), time);
        using var cancellation = new CancellationTokenSource();

        Ensure(Acquire(limiter), "old window must be exhausted");
        var pending = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        limiter.Update(2, TimeSpan.FromSeconds(10), DynamicFixedWindowActivationMode.NextWindowBoundary);
        cancellation.Cancel();

        try
        {
            await pending;
            throw new Exception("assert failed: cancelled waiter unexpectedly completed successfully");
        }
        catch (OperationCanceledException)
        {
        }

        Ensure(limiter.WaitingCount == 0, "cancelled waiter must be removed exactly once");
        time.Advance(TimeSpan.FromSeconds(20));
        Ensure(limiter.CurrentPermitLimit == 2, "pending update must still activate after unrelated waiter cancellation");
    }

    [Test]
    public void ImmediateUpdateSupersedesPreviouslyPendingDefinition()
    {
        var time = new ManualTimeProvider();
        using var limiter = new DynamicFixedWindowRateLimiter(2, TimeSpan.FromSeconds(30), time);

        Ensure(Acquire(limiter), "first permit must be charged");
        limiter.Update(10, TimeSpan.FromSeconds(5), DynamicFixedWindowActivationMode.NextWindowBoundary);
        limiter.Update(3, TimeSpan.FromSeconds(15), DynamicFixedWindowActivationMode.Immediate);

        Ensure(!limiter.HasPendingUpdate, "immediate winning update must supersede older pending definition");
        Ensure(limiter.CurrentPermitLimit == 3 && limiter.CurrentWindow == TimeSpan.FromSeconds(15),
            "immediate definition must become current atomically");
        Ensure(limiter.Consumed == 1, "immediate supersession must preserve charged consumption");
    }

    private static bool Acquire(DynamicFixedWindowRateLimiter limiter)
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
                {
                    if (timer.TakeIfDueLocked(_timestamp))
                        due.Add(timer);
                }
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
