using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableStorageTests
{
    private static readonly ReadOnlySequence<byte> SValue = new(new byte[] { 42, 0, 0, 0 });

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
    public async Task SerialRegistrationsShouldNotMaterializeFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        for (var index = 0; index < 8; index++)
        {
            var operation = table.Rent<int>(out var id);
            Ensure(table.Count == 1 && table.ActiveCount == 1, "registration must publish exactly once");
            await FinishResponseAsync(table, id, operation);
            Ensure(!table.SlotsMaterialized, "serial use must not allocate the full array");
            Ensure(table.Count == 0 && table.ActiveCount == 0, "completion must release capacity exactly once");
        }
    }

    [Test]
    public async Task OriginalRequestShouldExpireAfterOverlappingRequestPromotesStorage()
    {
        var clock = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(timeProvider: clock);
        var delay = TimeSpan.FromMilliseconds(10);
        var first = table.Rent(Int32Codec.Instance, PendingCallKind.Unary,
            RpcDeadline.Create(delay, clock), CancellationToken.None, out var firstId);
        var second = table.Rent<int>(out var secondId);
        Ensure(table.SlotsMaterialized && table.Contains(firstId) && table.Contains(secondId),
            "promotion must publish the new request without moving or losing the original");
        Ensure(table.DeadlineSlotSpan == table.Capacity + 1,
            "the live inline request must still be included in deadline scans");
        await FinishResponseAsync(table, secondId, second);

        clock.Advance(delay - TimeSpan.FromTicks(1));
        Ensure(table.Contains(firstId), "the original deadline must not complete early");
        clock.Advance(TimeSpan.FromTicks(1));
        var failure = await CaptureExceptionAsync(first.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the deadline scheduler must still reach the original inline request");
        Ensure(table.Count == 0 && table.ActiveCount == 0 && table.SlotsMaterialized,
            "promotion remains in place and releases exactly the two reservations");
    }

    [Test]
    public async Task OriginalCancellationShouldNotAffectPromotedRequest()
    {
        using var table = PendingRequestTableTestFixture.Create(2);
        using var cancellation = new CancellationTokenSource();
        var first = table.Rent(Int32Codec.Instance, PendingCallKind.Unary,
            default, cancellation.Token, out var firstId);
        var second = table.Rent<int>(out var secondId);
        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(first.AsValueTask().AsTask());
        Ensure(failure is OperationCanceledException { CancellationToken: var token } && token == cancellation.Token,
            "the original cancellation must be delivered to its own operation");
        Ensure(!table.Contains(firstId) && table.Contains(secondId),
            "inline completion must not remove the dense call");
        var third = table.Rent<int>(out var thirdId);
        Ensure(table.DeadlineSlotSpan == table.Capacity,
            "the next registration must settle on the flat route after the inline request exits");
        Ensure((firstId & 1) == (thirdId & 1), "the old ring index must be available again");
        Ensure(!table.TryComplete(firstId, PendingCallCompletionReason.ConnectionClosed),
            "the old full ID must not claim the newly registered call in the same ring index");
        await FinishResponseAsync(table, secondId, second);
        await FinishResponseAsync(table, thirdId, third);
        Ensure(table.Count == 0 && table.ActiveCount == 0, "all reservations must be released");
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

    private static async Task FinishResponseAsync(PendingRequestTable table, long id, RpcRequestOperation<int> operation)
    {
        var payload = SValue;
        Ensure(table.Dispatch(id, ref payload), "response must rediscover the original cell");
        Ensure(await operation.AsValueTask() == 42, "response value must reach the owning operation");
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
