using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionPartitionPoolTests
{
    [Test]
    public void RecentlyIdleReleaseShouldNotEnumeratePartitionTable()
    {
        var time = new ManualTimeProvider();
        var key = string.Empty;
        using var pool = CreatePool(() => key, maxPartitions: 1024, time: time);
        var context = CreateContext();

        for (var index = 0; index < 1024; index++)
        {
            key = $"partition-{index}";
            var lease = pool.TryAcquire(context);
            Ensure(lease is not null, $"partition {index} should be admitted");
            lease.Dispose();
        }

        Ensure(pool.Count == 1024, "all recently idle partitions should remain resident");
        Ensure(pool.ReclaimScanCount == 0, "setup should not scan before the idle deadline");
        var visitedBefore = pool.ReclaimEntriesVisited;

        key = "partition-0";
        var reacquired = pool.TryAcquire(context);
        Ensure(reacquired is not null, "existing partition should be reacquired");
        reacquired.Dispose();

        Ensure(pool.ReclaimScanCount == 0,
            "normal release before the earliest idle deadline must not start a reclaim scan");
        Ensure(pool.ReclaimEntriesVisited == visitedBefore,
            "normal release before the earliest idle deadline must visit zero dictionary entries");
    }

    [Test]
    public void ReclaimShouldHonorExactIdleTimeoutBoundary()
    {
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        var key = "first";
        using var pool = CreatePool(() => key, maxPartitions: 1, idleTimeout: timeout, time: time);
        var context = CreateContext();

        var first = pool.TryAcquire(context);
        Ensure(first is not null, "first partition should be admitted");
        first.Dispose();

        time.Advance(timeout - TimeSpan.FromTicks(1));
        key = "second";
        Ensure(pool.TryAcquire(context) is null,
            "partition must not be reclaimed one tick before IdleTimeout");
        Ensure(pool.ReclaimScanCount == 0,
            "capacity check before the deadline should use the O(1) hint");

        time.Advance(TimeSpan.FromTicks(1));
        var second = pool.TryAcquire(context);
        Ensure(second is not null, "partition should be reclaimable at exact IdleTimeout");
        Ensure(pool.Count == 1, "reclaimed capacity should be reused by the new key");
        Ensure(pool.ReclaimScanCount == 1 && pool.ReclaimEntriesVisited == 1,
            "exact timeout should trigger one bounded reconciliation scan");
        second.Dispose();
    }

    [Test]
    public void StaleEarlierHintMustNotRemoveReacquiredActiveEntry()
    {
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        var key = "a";
        using var pool = CreatePool(() => key, maxPartitions: 2, idleTimeout: timeout, time: time);
        var context = CreateContext();

        var first = pool.TryAcquire(context)!;
        var runtime = first.Runtime;
        first.Dispose();

        time.Advance(timeout);
        var active = pool.TryAcquire(context)!;
        Ensure(ReferenceEquals(active.Runtime, runtime), "reacquire should retain the existing runtime");

        key = "b";
        var second = pool.TryAcquire(context);
        Ensure(second is not null, "a stale due hint should reconcile and still admit another key");
        Ensure(pool.Count == 2, "the active reacquired entry must survive stale-hint reconciliation");
        Ensure(pool.ReclaimScanCount == 1, "stale due hint should cause one reconciliation scan");

        second.Dispose();
        active.Dispose();
    }

    [Test]
    public void ReIdleShouldRecomputeDeadlineAfterStaleHint()
    {
        var timeout = TimeSpan.FromTicks(10);
        var halfTimeout = TimeSpan.FromTicks(5);
        var time = new ManualTimeProvider();
        var key = "a";
        using var pool = CreatePool(() => key, maxPartitions: 1, idleTimeout: timeout, time: time);
        var context = CreateContext();

        pool.TryAcquire(context)!.Dispose();
        time.Advance(halfTimeout);
        pool.TryAcquire(context)!.Dispose();

        time.Advance(halfTimeout);
        key = "b";
        Ensure(pool.TryAcquire(context) is null,
            "the old idle lifetime must not reclaim the re-idled entry early");
        Ensure(pool.ReclaimScanCount == 1,
            "the stale old deadline should reconcile exactly once");

        time.Advance(halfTimeout - TimeSpan.FromTicks(1));
        Ensure(pool.TryAcquire(context) is null,
            "the refreshed idle deadline must still reject one tick early");
        Ensure(pool.ReclaimScanCount == 1,
            "the refreshed future hint should prevent another scan before it is due");

        time.Advance(TimeSpan.FromTicks(1));
        var replacement = pool.TryAcquire(context);
        Ensure(replacement is not null, "the re-idled entry should be reclaimable at its own deadline");
        Ensure(pool.ReclaimScanCount == 2, "the refreshed deadline should trigger the next scan");
        replacement.Dispose();
    }

    [Test]
    public void FullPoolShouldNotEvictActiveOrRecentlyIdleEntries()
    {
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        var key = "a";
        using var pool = CreatePool(() => key, maxPartitions: 1, idleTimeout: timeout, time: time);
        var context = CreateContext();

        var active = pool.TryAcquire(context)!;
        key = "b";
        Ensure(pool.TryAcquire(context) is null, "active partition must not be evicted for capacity");
        Ensure(pool.ReclaimScanCount == 0, "no idle hint means no capacity reclaim scan is needed");

        active.Dispose();
        Ensure(pool.TryAcquire(context) is null,
            "recently idle partition must not be evicted before IdleTimeout");
        Ensure(pool.ReclaimScanCount == 0,
            "recently idle capacity rejection should stay on the O(1) hint path");
    }

    [Test]
    public void LargeTimeJumpShouldReclaimAllExpiredEntriesInOneReconciliation()
    {
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        var key = string.Empty;
        using var pool = CreatePool(() => key, maxPartitions: 128, idleTimeout: timeout, time: time);
        var context = CreateContext();

        for (var index = 0; index < 128; index++)
        {
            key = $"partition-{index}";
            pool.TryAcquire(context)!.Dispose();
        }

        time.Advance(TimeSpan.FromTicks(1000));
        key = "replacement";
        var replacement = pool.TryAcquire(context);
        Ensure(replacement is not null, "expired partitions should release capacity after a large time jump");
        Ensure(pool.Count == 1, "one reconciliation should detach every expired idle entry");
        Ensure(pool.ReclaimScanCount == 1 && pool.ReclaimEntriesVisited == 128,
            "large jump should require one full scan, not repeated per-release scans");
        replacement.Dispose();
    }

    [Test]
    public void DisposeThenLeaseReleaseShouldNotResurrectIdleHintState()
    {
        var time = new ManualTimeProvider();
        var key = "a";
        var pool = CreatePool(() => key, maxPartitions: 1, time: time);
        var lease = pool.TryAcquire(CreateContext())!;

        pool.Dispose();
        lease.Dispose();
        lease.Dispose();

        Ensure(pool.Count == 0, "disposed pool should remain empty after late lease release");
        Ensure(pool.ReclaimScanCount == 0 && pool.ReclaimEntriesVisited == 0,
            "late release after pool disposal must not start reclaim work");
    }

    private static AdmissionPartitionPool CreatePool(
        Func<string?> selector,
        int maxPartitions,
        TimeSpan? idleTimeout = null,
        ManualTimeProvider? time = null)
    {
        var options = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = maxPartitions,
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5)
        };
        options.UseConcurrency(1);
        return new AdmissionPartitionPool(_ => selector(), options, queueLimit: 0, time ?? new ManualTimeProvider());
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(1, 2, RpcMethodKind.Unary, "partition-test", null, null, null);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
            Interlocked.Add(ref _timestamp, amount.Ticks);
        }
    }
}
