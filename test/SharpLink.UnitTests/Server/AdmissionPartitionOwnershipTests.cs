using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionPartitionOwnershipTests
{
    [Test]
    public async Task RequestAndLeaseShouldReleasePartitionEntryExactlyOnce()
    {
        var partitionOptions = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = 1,
            IdleTimeout = TimeSpan.FromMinutes(1)
        };
        partitionOptions.UseConcurrency(1);
        using var pool = new AdmissionPartitionPool(
            _ => "hot",
            partitionOptions,
            queueLimit: 0,
            TimeProvider.System);
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-305-test", null, null);

        var ownerOptions = new SharpLinkAdmissionControlOptions();
        ownerOptions.Global.UseConcurrency(1);
        await using var owner = SharpLinkAdmissionController.Create(ownerOptions, []);

        var firstEntry = pool.TryAcquire(context)!;
        Ensure(firstEntry.References == 1, "first partition reference acquired");
        var slots = new AdmissionLimiterSlot[firstEntry.Runtime.SlotCount];
        var count = 0;
        firstEntry.Runtime.AppendTo(slots, ref count);
        using var firstRequest = new AdmissionRequest(slots, count, firstEntry);
        Ensure(firstRequest.TryAcquire(owner, out var firstLease, out _),
            "first request should acquire the partition limiter");

        firstRequest.Dispose();
        Ensure(firstEntry.References == 1,
            "successful request transfers rather than releases partition ownership");

        var secondEntry = pool.TryAcquire(context)!;
        Ensure(ReferenceEquals(firstEntry, secondEntry), "same key should reuse the entry");
        Ensure(firstEntry.References == 2, "second request adds one partition reference");
        var secondSlots = new AdmissionLimiterSlot[secondEntry.Runtime.SlotCount];
        count = 0;
        secondEntry.Runtime.AppendTo(secondSlots, ref count);
        using var secondRequest = new AdmissionRequest(secondSlots, count, secondEntry);
        Ensure(!secondRequest.TryAcquire(owner, out _, out _),
            "second request should reject while the first concurrency permit is active");

        secondRequest.Dispose();
        secondRequest.Dispose();
        Ensure(firstEntry.References == 1,
            "rejected request releases its partition reference exactly once");

        firstLease!.Dispose();
        firstLease.Dispose();
        Ensure(firstEntry.References == 0,
            "admitted lease releases its transferred partition reference exactly once");
    }

    [Test]
    public async Task MultiSlotPartialAcquireShouldRollbackAndReleasePartitionExactlyOnce()
    {
        var idleTimeout = TimeSpan.FromTicks(10);
        var time = new ManualTimeProvider();
        var key = "hot";
        var partitionOptions = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = 1,
            IdleTimeout = idleTimeout
        };
        partitionOptions.UseConcurrency(1);
        using var pool = new AdmissionPartitionPool(
            _ => key,
            partitionOptions,
            queueLimit: 0,
            time);
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-305-partial-rollback", null, null);

        var ownerOptions = new SharpLinkAdmissionControlOptions();
        ownerOptions.Global.UseConcurrency(1);
        await using var owner = SharpLinkAdmissionController.Create(ownerOptions, []);

        var heldEntry = pool.TryAcquire(context)!;
        var heldSlots = new AdmissionLimiterSlot[heldEntry.Runtime.SlotCount];
        var count = 0;
        heldEntry.Runtime.AppendTo(heldSlots, ref count);
        using var heldRequest = new AdmissionRequest(heldSlots, count, heldEntry);
        Ensure(heldRequest.TryAcquire(owner, out var heldLease, out _),
            "setup request should hold the partition concurrency permit");
        heldRequest.Dispose();
        Ensure(heldEntry.References == 1,
            "setup ownership should transfer to its admitted lease");

        var candidateEntry = pool.TryAcquire(context)!;
        Ensure(ReferenceEquals(heldEntry, candidateEntry), "same key should reuse the resident entry");
        Ensure(candidateEntry.References == 2,
            "candidate request should own a second partition reference before slot acquisition");

        using var upstream = new TrackingRateLimiter();
        var candidateSlots = new AdmissionLimiterSlot[1 + candidateEntry.Runtime.SlotCount];
        candidateSlots[0] = new AdmissionLimiterSlot(
            upstream,
            "global",
            "concurrency",
            RetainOnFailure: false);
        count = 1;
        candidateEntry.Runtime.AppendTo(candidateSlots, ref count);
        using var candidateRequest = new AdmissionRequest(candidateSlots, count, candidateEntry);

        Ensure(!candidateRequest.TryAcquire(owner, out _, out var failedSlot),
            "later partition limiter should fail while its permit is held");
        Ensure(failedSlot.Reason == "concurrency",
            "the failed downstream slot should be the partition concurrency limiter");
        Ensure(upstream.AttemptCount == 1 && upstream.LastLease?.DisposeCount == 1,
            "the earlier non-partition acquisition must be rolled back exactly once");
        Ensure(candidateEntry.References == 2,
            "slot rollback must not separately release request-owned partition state");

        candidateRequest.Dispose();
        candidateRequest.Dispose();
        Ensure(candidateEntry.References == 1,
            "failed multi-slot request must release its partition reference exactly once");

        heldLease!.Dispose();
        heldLease.Dispose();
        Ensure(candidateEntry.References == 0,
            "remaining admitted lease should release the last active partition reference");

        time.Advance(idleTimeout);
        key = "replacement";
        var replacement = pool.TryAcquire(context);
        Ensure(replacement is not null && !ReferenceEquals(replacement, candidateEntry),
            "released partition ownership should make MaxPartitions capacity reclaimable");
        pool.Release(replacement!);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TrackingRateLimiter : RateLimiter
    {
        internal int AttemptCount { get; private set; }
        internal TrackingRateLimitLease? LastLease { get; private set; }
        public override TimeSpan? IdleDuration => null;
        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            _ = permitCount;
            AttemptCount++;
            return LastLease = new TrackingRateLimitLease();
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(AttemptAcquireCore(permitCount));
        }
    }

    private sealed class TrackingRateLimitLease : RateLimitLease
    {
        internal int DisposeCount { get; private set; }
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            _ = metadataName;
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            _ = disposing;
            DisposeCount++;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void Advance(TimeSpan value) =>
            Interlocked.Add(ref _timestamp, value.Ticks);
    }
}
