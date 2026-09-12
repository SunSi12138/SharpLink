using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowPendingPolicyLifecycleTests
{
    [Test]
    public void PublishedPendingPolicyShouldSurviveItsProgramRetirementForLiveFixedSnapshots()
    {
        var time = new ManualTimeProvider();
        using var source = CreateFixed(1, TimeSpan.FromSeconds(10), time);
        Ensure(Acquire(source), "source FixedWindow must consume its only current-window permit");

        var pending = CreateFixed(2, TimeSpan.FromSeconds(20), time, source);
        source.CommitTransitionTo(pending);
        pending.OnPublished();
        Ensure(pending.FixedWindowForTests!.HasPendingWindowForTests,
            "published changed Window must remain pending until the source natural boundary");

        using var token = CreateTokenBucket(1, time, pending);
        pending.CommitTransitionTo(token);
        pending.Dispose();

        Ensure(Acquire(token), "algorithm replacement must start a fresh TokenBucket generation");
        Ensure(!Acquire(token), "fresh TokenBucket generation must enforce its own one-token budget");

        time.Advance(TimeSpan.FromSeconds(10));
        Ensure(source.FixedWindowForTests!.ActiveWindowForTests == TimeSpan.FromSeconds(20),
            "the already-published pending FixedWindow policy must still activate for surviving old snapshots");
        Ensure(source.FixedWindowForTests.ActiveLimitForTests == 2,
            "pending FixedWindow capacity must activate even after its owning Program retires");
        Ensure(Acquire(source) && Acquire(source) && !Acquire(source),
            "surviving FixedWindow snapshots must converge to the activated two-permit shared ledger");
        Ensure(!Acquire(token),
            "old FixedWindow boundary activity must remain isolated from the fresh TokenBucket generation");
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

    private static AdmissionRateState CreateTokenBucket(
        int tokenLimit,
        TimeProvider timeProvider,
        AdmissionRateState source)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseTokenBucket(options =>
        {
            options.TokenLimit = tokenLimit;
            options.TokensPerPeriod = tokenLimit;
            options.ReplenishmentPeriod = TimeSpan.FromHours(1);
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
            Interlocked.Add(ref _timestamp, delta.Ticks);
        }
    }
}
