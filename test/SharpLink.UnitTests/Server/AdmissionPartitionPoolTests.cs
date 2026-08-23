using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            pool.Release(lease!);
        }

        Ensure(pool.Count == 1024, "all recently idle partitions should remain resident");
        Ensure(pool.ReclaimScanCount == 0, "setup should not scan before the idle deadline");
        var visitedBefore = pool.ReclaimEntriesVisited;

        key = "partition-0";
        var reacquired = pool.TryAcquire(context);
        Ensure(reacquired is not null, "existing partition should be reacquired");
        pool.Release(reacquired!);

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
        pool.Release(first!);

        time.Advance(timeout - TimeSpan.FromTicks(1));
        key = "second";
        Ensure(pool.TryAcquire(context) is null,
            "partition must not be reclaimed one tick before IdleTimeout");
        Ensure(pool.Count == 1, "recently idle partition must remain resident before the deadline");
        Ensure(pool.ReclaimScanCount == 0,
            "capacity check before the deadline should use the O(1) hint");

        time.Advance(TimeSpan.FromTicks(1));
        var second = pool.TryAcquire(context);
        Ensure(second is not null, "partition should be reclaimable at exact IdleTimeout");
        Ensure(pool.Count == 1, "reclaimed capacity should be reused by the new key");
        Ensure(pool.ReclaimScanCount == 1 && pool.ReclaimEntriesVisited == 1,
            "exact timeout should trigger one bounded reconciliation scan");
        pool.Release(second!);
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
        pool.Release(first);

        time.Advance(timeout);
        var active = pool.TryAcquire(context)!;
        Ensure(ReferenceEquals(active.Runtime, runtime), "reacquire should retain the existing runtime");

        key = "b";
        var second = pool.TryAcquire(context);
        Ensure(second is not null, "a stale due hint should reconcile and still admit another key");
        Ensure(pool.Count == 2, "the active reacquired entry must survive stale-hint reconciliation");
        Ensure(pool.ReclaimScanCount == 1, "stale due hint should cause one reconciliation scan");

        pool.Release(second!);
        pool.Release(active);
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

        pool.Release(pool.TryAcquire(context)!);
        time.Advance(halfTimeout);
        pool.Release(pool.TryAcquire(context)!);

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
        pool.Release(replacement!);
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
        Ensure(pool.Count == 1, "active partition should remain resident at capacity");
        Ensure(pool.ReclaimScanCount == 0, "no idle hint means no capacity reclaim scan is needed");

        pool.Release(active);
        Ensure(pool.TryAcquire(context) is null,
            "recently idle partition must not be evicted before IdleTimeout");
        Ensure(pool.Count == 1, "recently idle partition should remain resident at capacity");
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
            pool.Release(pool.TryAcquire(context)!);
        }

        time.Advance(TimeSpan.FromTicks(1000));
        key = "replacement";
        var replacement = pool.TryAcquire(context);
        Ensure(replacement is not null, "expired partitions should release capacity after a large time jump");
        Ensure(pool.Count == 1, "one reconciliation should detach every expired idle entry");
        Ensure(pool.ReclaimScanCount == 1 && pool.ReclaimEntriesVisited == 128,
            "large jump should require one full scan, not repeated per-release scans");
        pool.Release(replacement!);
    }

    [Test]
    public void ConcurrentSameKeyAcquireReleaseShouldReturnReferenceCountToZero()
    {
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        using var pool = CreatePool(
            static context => context.ConnectionId,
            maxPartitions: 1,
            idleTimeout: timeout,
            time: time);
        var shared = CreateContext("shared");

        Parallel.For(0, 8, worker =>
        {
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var lease = pool.TryAcquire(shared);
                Ensure(lease is not null, $"worker {worker} should acquire the shared partition");
                if ((iteration & 31) == 0)
                    Thread.Yield();
                pool.Release(lease!);
            }
        });

        Ensure(pool.Count == 1, "same-key concurrency should keep exactly one partition entry");
        time.Advance(timeout);
        var replacement = pool.TryAcquire(CreateContext("replacement"));
        Ensure(replacement is not null,
            "after all concurrent leases release, the shared entry must be idle and reclaimable");
        Ensure(pool.Count == 1,
            "successful replacement proves reference accounting did not underflow or leak active references");
        pool.Release(replacement!);
    }

    [Test]
    public void ConcurrentMultiKeyReleaseReacquireShouldNotLeakEntriesOrReferences()
    {
        const int partitions = 16;
        var timeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        using var pool = CreatePool(
            static context => context.ConnectionId,
            maxPartitions: partitions,
            idleTimeout: timeout,
            time: time);
        var contexts = Enumerable.Range(0, partitions)
            .Select(static index => CreateContext($"partition-{index}"))
            .ToArray();

        Parallel.For(0, partitions, index =>
        {
            var context = contexts[index];
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var lease = pool.TryAcquire(context);
                Ensure(lease is not null, $"partition {index} should remain acquirable");
                if ((iteration & 63) == 0)
                    Thread.Yield();
                pool.Release(lease!);
            }
        });

        Ensure(pool.Count == partitions, "all partition identities should remain resident before timeout");
        time.Advance(timeout);
        var replacement = pool.TryAcquire(CreateContext("replacement"));
        Ensure(replacement is not null, "expired multi-key entries should release capacity after concurrency");
        Ensure(pool.Count == 1,
            "one reconciliation should detach every expired idle entry without leaked references");
        pool.Release(replacement!);
    }

    [Test]
    public void SelectorExceptionShouldPropagateWithoutMutatingPool()
    {
        var failure = new InvalidOperationException("selector failure");
        var calls = 0;
        using var pool = CreatePool(
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw failure;
            },
            maxPartitions: 1);

        try
        {
            _ = pool.TryAcquire(CreateContext());
            throw new InvalidOperationException("selector exception should have propagated");
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure))
        {
        }

        Ensure(calls == 1, "partition selector should still be invoked exactly once per acquire attempt");
        Ensure(pool.Count == 0, "selector failure must not mutate the partition table");
        Ensure(pool.ReclaimScanCount == 0 && pool.ReclaimEntriesVisited == 0,
            "selector failure must not enter reclaim logic");
    }

    [Test]
    public void DisposeReleaseAndReclaimRaceShouldRemainSafe()
    {
        const int rounds = 32;
        var timeout = TimeSpan.FromTicks(10);

        for (var round = 0; round < rounds; round++)
        {
            var time = new ManualTimeProvider();
            var pool = CreatePool(
                static context => context.ConnectionId,
                maxPartitions: 2,
                idleTimeout: timeout,
                time: time);
            pool.Release(pool.TryAcquire(CreateContext("expired"))!);
            var active = pool.TryAcquire(CreateContext("active"))!;
            time.Advance(timeout);

            using var start = new ManualResetEventSlim(false);
            AdmissionPartitionEntry? replacement = null;
            var reclaim = Task.Run(() =>
            {
                start.Wait();
                replacement = pool.TryAcquire(CreateContext("replacement"));
            });
            var dispose = Task.Run(() =>
            {
                start.Wait();
                pool.Dispose();
            });
            var release = Task.Run(() =>
            {
                start.Wait();
                pool.Release(active);
            });

            start.Set();
            Task.WaitAll(reclaim, dispose, release);
            if (replacement is not null)
                pool.Release(replacement);
            pool.Dispose();

            Ensure(pool.Count == 0,
                $"round {round}: dispose/release/reclaim race must leave the pool empty");
        }
    }

    [Test]
    public void HighChurnShouldKeepResidentStateBoundedWithoutReclaimQueueGrowth()
    {
        const int maxPartitions = 64;
        const int churnOperations = 100_000;
        var time = new ManualTimeProvider();
        using var pool = CreatePool(
            static context => context.ConnectionId,
            maxPartitions: maxPartitions,
            time: time);
        var contexts = Enumerable.Range(0, maxPartitions)
            .Select(static index => CreateContext($"partition-{index}"))
            .ToArray();

        for (var operation = 0; operation < churnOperations; operation++)
            pool.Release(pool.TryAcquire(contexts[operation % maxPartitions])!);

        Ensure(pool.Count == maxPartitions,
            "100k idle/reacquire churn operations must not grow resident entries beyond MaxPartitions");
        Ensure(pool.ReclaimScanCount == 0 && pool.ReclaimEntriesVisited == 0,
            "frozen time should keep 100k churn operations entirely on the scalar O(1) hint path");
        Ensure(pool.TryAcquire(CreateContext("overflow")) is null,
            "a new key must still respect MaxPartitions while all resident entries are recently idle");
        Ensure(pool.Count == maxPartitions,
            "capacity rejection must not create any extra resident reclaim state");
    }

    [Test]
    public void DisposeThenLeaseReleaseShouldNotResurrectIdleHintState()
    {
        var time = new ManualTimeProvider();
        var key = "a";
        var pool = CreatePool(() => key, maxPartitions: 1, time: time);
        var lease = pool.TryAcquire(CreateContext())!;

        pool.Dispose();
        pool.Release(lease);

        Ensure(pool.Count == 0, "disposed pool should remain empty after late lease release");
        Ensure(pool.ReclaimScanCount == 0 && pool.ReclaimEntriesVisited == 0,
            "late release after pool disposal must not start reclaim work");
    }

    private static AdmissionPartitionPool CreatePool(
        Func<string?> selector,
        int maxPartitions,
        TimeSpan? idleTimeout = null,
        ManualTimeProvider? time = null)
        => CreatePool(_ => selector(), maxPartitions, idleTimeout, time);

    private static AdmissionPartitionPool CreatePool(
        Func<SharpLinkAdmissionContext, string?> selector,
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
        return new AdmissionPartitionPool(
            selector,
            options,
            queueLimit: 0,
            time ?? new ManualTimeProvider());
    }

    private static SharpLinkAdmissionContext CreateContext(string connectionId = "partition-test")
        => new(1, 2, RpcMethodKind.Unary, connectionId, null, null, null);

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
