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
            1, 2, RpcMethodKind.Unary, "issue-305-test", null, null, null);

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
