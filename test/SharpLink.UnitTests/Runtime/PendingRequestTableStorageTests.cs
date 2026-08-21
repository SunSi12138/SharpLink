using System.Buffers;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableStorageTests
{
    [Test]
    public void IdleTableShouldNotMaterializeFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        Ensure(table.Capacity == 65_536, "logical capacity must remain unchanged");
        Ensure(!table.SlotsMaterialized, "idle construction must not allocate the full slot array");
        Ensure(table.Count == 0 && table.ActiveCount == 0, "idle counts must remain zero");
    }

    [Test]
    public void ReadOnlyLookupsShouldNotMaterializeFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        const long missingId = 700;
        Ensure(!table.Contains(missingId), "contains should reject a missing request");
        Ensure(!table.TryComplete(missingId, PendingCallCompletionReason.UserCancellation),
            "terminal lookup should reject a missing request");
        Ensure(table.GetProducerCancellationToken(missingId).IsCancellationRequested,
            "producer token lookup should preserve the missing-call contract");
        Ensure(!table.SlotsMaterialized, "read-only lookups must not allocate flat slots");
    }

    [Test]
    public async Task FirstRegistrationShouldMaterializeAndRetainFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        var operation = table.Rent<int>(out var id);
        Ensure(table.SlotsMaterialized, "first real registration must materialize flat slots");
        Ensure(table.Count == 1 && table.ActiveCount == 1, "registration must publish exactly once");
        await CompleteForCleanup(table, id, operation);
        Ensure(table.SlotsMaterialized, "flat slots are retained after first materialization");
        Ensure(table.Count == 0 && table.ActiveCount == 0, "completion must release capacity exactly once");
    }

    [Test]
    public async Task ConcurrentFirstUseShouldPublishAllCalls()
    {
        const int count = 32;
        using var table = PendingRequestTableTestFixture.Create(65_536);
        var ids = new long[count];
        var operations = new RpcRequestOperation<int>[count];
        Parallel.For(0, count, i => operations[i] = table.Rent<int>(out ids[i]));
        Ensure(table.SlotsMaterialized, "concurrent first use must converge on one published flat table");
        Ensure(table.Count == count && table.ActiveCount == count, "all reserved calls must publish");
        for (var i = 0; i < count; i++)
            await CompleteForCleanup(table, ids[i], operations[i]);
        Ensure(table.ActiveCount == 0, "concurrent cleanup must release every reservation");
    }

    [Test]
    public async Task SparseDeadlineScanShouldInspectOnePage()
    {
        var timeProvider = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(65_536, timeProvider: timeProvider);
        var deadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
        var operation = table.Rent(new Int32Codec(), PendingCallKind.Unary, deadline,
            CancellationToken.None, out _);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "deadline expiration must preserve the terminal result");
        Ensure(table.LastDeadlineScanInspectedSlots == 256,
            "one sparse deadline page should inspect 256 slots, not full capacity");
        Ensure(table.ActiveCount == 0, "deadline completion must release capacity");
    }

    [Test]
    public async Task CompletedDeadlinePageShouldClearWhenNextScanConsumesMark()
    {
        var timeProvider = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(65_536, timeProvider: timeProvider);
        var farDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddMinutes(5), timeProvider);
        var first = table.Rent(new Int32Codec(), PendingCallKind.Unary, farDeadline,
            CancellationToken.None, out var firstId);
        await CompleteForCleanup(table, firstId, first);

        for (var i = 0; i < 254; i++)
        {
            var operation = table.Rent<int>(out var id);
            await CompleteForCleanup(table, id, operation);
        }

        var expiring = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
        var second = table.Rent(new Int32Codec(), PendingCallKind.Unary, expiring,
            CancellationToken.None, out var secondId);
        Ensure((secondId >> 8) != (firstId >> 8), "test must move the deadline call to the next page");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var failure = await CaptureExceptionAsync(second.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "second-page deadline must expire normally");
        Ensure(table.LastDeadlineScanInspectedSlots == 512,
            "the next scan should consume the retired page mark once while inspecting the active page");

        var nextDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
        var third = table.Rent(new Int32Codec(), PendingCallKind.Unary, nextDeadline,
            CancellationToken.None, out _);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        failure = await CaptureExceptionAsync(third.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a later deadline on the active page must still expire normally");
        Ensure(table.LastDeadlineScanInspectedSlots == 256,
            "once consumed, the retired page mark must not widen later deadline scans");
    }

    private static async Task CompleteForCleanup(PendingRequestTable table, long id, RpcRequestOperation<int> operation)
    {
        var cleanup = new IOException("pending storage test cleanup");
        Ensure(table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed, cleanup),
            "cleanup should win the pending slot exactly once");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(ReferenceEquals(failure, cleanup), "cleanup exception should flow through the operation");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
