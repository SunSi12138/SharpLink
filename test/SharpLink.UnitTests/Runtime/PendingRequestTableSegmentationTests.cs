using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableSegmentationTests
{
    [Test]
    public void MaximumCapacityShouldStartWithOnlySegmentDirectoryStorage()
    {
        var slots = new SegmentedSlotTable<object>(1024 * 1024);

        Ensure(slots.Length == 1024 * 1024, "logical capacity must remain unchanged");
        Ensure(slots.SegmentSize == 256, "large pending tables should use 256-slot segments");
        Ensure(slots.SegmentCount == 4096, "the hard maximum should require only 4096 root references");
        Ensure(slots.MaterializedSegmentCount == 0, "construction must not materialize a slot segment");

        _ = slots.Read(700_000);
        Ensure(slots.MaterializedSegmentCount == 0,
            "a lookup into an untouched segment must not materialize storage");
    }

    [Test]
    public async Task FirstRegistrationShouldMaterializeOnlyOneSegment()
    {
        using var manager = PendingRequestTableTestFixture.Create(65_536);
        Ensure(manager.MaterializedSegmentCount == 0,
            "an idle default-capacity table must not materialize slot segments");

        var operation = manager.Rent<int>(out var id);

        Ensure(id != 0, "request IDs must remain non-zero");
        Ensure(manager.MaterializedSegmentCount == 1,
            "the first registration should materialize exactly one segment");
        Ensure(manager.Count == 1 && manager.ActiveCount == 1,
            "the authoritative capacity count should report the active request");

        await CompleteForCleanup(manager, id, operation);
        Ensure(manager.Count == 0, "completion should return the authoritative active count to zero");
        Ensure(manager.MaterializedSegmentCount == 1,
            "the first implementation intentionally retains materialized segments until table disposal");
    }

    [Test]
    public void NullSegmentLookupShouldStayAllocationFreeAndReturnNoMatch()
    {
        using var manager = PendingRequestTableTestFixture.Create(1024);
        const long untouchedId = 700;

        Ensure(!manager.Contains(untouchedId), "contains should reject an untouched segment");
        Ensure(!manager.TryComplete(untouchedId, PendingCallCompletionReason.UserCancellation),
            "terminal lookup should reject an untouched segment");
        Ensure(manager.GetProducerCancellationToken(untouchedId).IsCancellationRequested,
            "producer token lookup should preserve the stale/missing-call contract");
        Ensure(manager.MaterializedSegmentCount == 0,
            "read-only lookups must not materialize untouched segments");
    }

    [Test]
    public async Task ConcurrentRegistrationsInOneSegmentShouldPublishOneLiveSegment()
    {
        const int registrationCount = 32;
        using var manager = PendingRequestTableTestFixture.Create(256);
        var ids = new long[registrationCount];
        var operations = new RpcRequestOperation<int>[registrationCount];

        Parallel.For(0, registrationCount, index =>
        {
            operations[index] = manager.Rent<int>(out ids[index]);
        });

        Ensure(manager.MaterializedSegmentCount == 1,
            "concurrent first-touch publication must converge on one live segment");
        Ensure(manager.Count == registrationCount,
            "all legitimate capacity reservations should publish successfully");

        for (var index = 0; index < registrationCount; index++)
            await CompleteForCleanup(manager, ids[index], operations[index]);

        Ensure(manager.Count == 0, "concurrent registrations must release capacity exactly once");
    }

    [Test]
    public async Task CrossingSegmentBoundaryShouldMaterializeOnlyTouchedSegments()
    {
        const int registrationCount = 257;
        using var manager = PendingRequestTableTestFixture.Create(1024);
        var ids = new long[registrationCount];
        var operations = new RpcRequestOperation<int>[registrationCount];

        for (var index = 0; index < registrationCount; index++)
            operations[index] = manager.Rent<int>(out ids[index]);

        Ensure(manager.MaterializedSegmentCount == 2,
            "257 sequential request IDs should touch exactly two 256-slot segments");

        for (var index = 0; index < registrationCount; index++)
            await CompleteForCleanup(manager, ids[index], operations[index]);

        Ensure(manager.Count == 0, "boundary coverage cleanup must release all capacity");
        Ensure(manager.MaterializedSegmentCount == 2,
            "segments should remain stable instead of being reclaimed during normal churn");
    }

    [Test]
    public async Task SparseDeadlineScanShouldInspectOnlyMaterializedSegments()
    {
        var timeProvider = new ManualTimeProvider();
        using var manager = PendingRequestTableTestFixture.Create(65_536, timeProvider: timeProvider);
        var deadline = RpcDeadline.Create(
            timeProvider.GetUtcNow().AddSeconds(1),
            timeProvider);
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline,
            CancellationToken.None,
            out _);

        Ensure(manager.MaterializedSegmentCount == 1,
            "the sparse deadline call should touch one segment before expiry");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "deadline processing must preserve the existing terminal result");
        Ensure(manager.LastDeadlineScanInspectedSlots == manager.SegmentSize,
            "deadline processing should inspect one materialized segment, not the configured capacity");
        Ensure(manager.LastDeadlineScanInspectedSlots == 256,
            "the default sparse scan should be bounded to 256 slots after first touch");
        Ensure(manager.Count == 0, "deadline completion must release capacity exactly once");
    }

    private static async Task CompleteForCleanup(
        PendingRequestTable manager,
        long id,
        RpcRequestOperation<int> operation)
    {
        var cleanup = new IOException("segmentation test cleanup");
        Ensure(manager.TryComplete(id, PendingCallCompletionReason.ConnectionClosed, cleanup),
            "cleanup should win the pending slot exactly once");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(ReferenceEquals(failure, cleanup), "cleanup exception should flow through the operation");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
