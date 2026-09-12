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
