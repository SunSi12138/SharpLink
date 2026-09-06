using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowActivationModeTests
{
    [Test]
    public void LimitOnlyNextWindowBoundaryShouldDeferTheEntireTarget()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(2, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "source permit one must succeed");
        Ensure(Acquire(source), "source permit two must succeed");

        using var target = CreateFixed(
            4,
            TimeSpan.FromSeconds(10),
            time,
            source,
            DynamicFixedWindowActivationMode.NextWindowBoundary);
        source.CommitTransitionTo(target);
        target.OnPublished();

        Ensure(target.FixedWindowForTests!.ActivationModeForTests ==
               DynamicFixedWindowActivationMode.NextWindowBoundary,
            "the explicit deferred selector must survive candidate construction");
        Ensure(target.FixedWindowForTests.HasPendingWindowForTests,
            "a limit-only deferred update still needs one pending target at the natural boundary");
        Ensure(target.FixedWindowForTests.QueuedLimitForTests == 2,
            "queued/current-window work must keep the old limit before the boundary");
        Ensure(!Acquire(target),
            "the extra two permits must not become visible before the natural boundary");

        time.Advance(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 4; index++)
            Ensure(Acquire(target), $"deferred target permit {index + 1} must succeed after rollover");
        Ensure(!Acquire(target), "the deferred four-permit target must be enforced");
    }

    [Test]
    public void ExplicitImmediateWindowChangeShouldFailBeforeMutatingTheLiveCounter()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(2, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "source permit one must succeed");
        var counterIdentity = source.FixedWindowForTests!.CounterIdentityForTests;
        var consumed = source.FixedWindowForTests.ConsumedForTests;

        Exception? failure = null;
        try
        {
            using var _ = CreateFixed(
                3,
                TimeSpan.FromSeconds(20),
                time,
                source,
                DynamicFixedWindowActivationMode.Immediate);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException,
            "Immediate must reject a Window change instead of silently changing time semantics");
        Ensure(source.FixedWindowForTests.CounterIdentityForTests == counterIdentity &&
               source.FixedWindowForTests.ConsumedForTests == consumed,
            "failed candidate construction must not alter live counter identity or consumption");
        Ensure(Acquire(source), "the remaining source permit must still be available after failure");
        Ensure(!Acquire(source), "failed candidate must not mint source quota");
    }

    private static AdmissionRateState CreateFixed(
        int permitLimit,
        TimeSpan window,
        TimeProvider timeProvider,
        AdmissionRateState? source = null,
        DynamicFixedWindowActivationMode? activation = null)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(options =>
        {
            options.PermitLimit = permitLimit;
            options.Window = window;
            options.UpdateActivation = activation;
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
