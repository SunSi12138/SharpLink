using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowChainedUpdateRegressionTests
{
    [Test]
    public void LimitOnlySuccessorMustNotBypassAPendingWindowActivation()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(2, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "source permit one must succeed");
        Ensure(Acquire(source), "source permit two must succeed");

        using var pending = CreateFixed(4, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(pending);
        pending.OnPublished();
        Ensure(pending.FixedWindowForTests!.HasPendingWindowForTests,
            "changed Window must remain pending until the old natural boundary");

        using var target = CreateFixed(5, TimeSpan.FromSeconds(20), time, pending);
        pending.CommitTransitionTo(target);
        target.OnPublished();

        Ensure(target.FixedWindowForTests!.ActivationModeForTests ==
               DynamicFixedWindowActivationMode.NextWindowBoundary,
            "a limit-only successor of a not-yet-active Window must remain boundary-deferred");
        Ensure(target.FixedWindowForTests.QueuedLimitForTests == 2,
            "the old current-window limit must stay authoritative before activation");
        Ensure(!Acquire(target),
            "the newer limit must not leak into the old active Window before its boundary");

        time.Advance(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 5; index++)
            Ensure(Acquire(target), $"target permit {index + 1} must succeed after activation");
        Ensure(!Acquire(target), "the activated target limit must be enforced");
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
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        internal void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));
            _timestamp = checked(_timestamp + delta.Ticks);
        }
    }
}
