namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Credit semantics for summing already-produced, legitimate updates of one stream.
/// These tests do not defer RecordConsumed or stand in for the pump's wire/ownership tests.
/// </summary>
public sealed class WindowUpdateCoalescingCreditTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegitimateCompletedUpdatesShouldReleaseIndependentStreamWaiters(bool combine)
    {
        var sender = new StreamFlowController(100, 100, 1024);
        var receiver = new StreamFlowController(100, 100, 1024);
        for (var index = 0; index < 3; index++)
            await TransferAsync(sender, receiver, 1, 20);
        await TransferAsync(sender, receiver, 2, 40);
        sender.CompleteSendStream(1, 1);
        Ensure(receiver.FlushConsumed(1, 1) == 0,
            "EOF before consumption must retain the entire outstanding receive debt");

        var first = sender.AcquireSendCreditAsync(3, 1, 30, CancellationToken.None);
        var second = sender.AcquireSendCreditAsync(4, 1, 30, CancellationToken.None);
        Ensure(!first.IsCompleted && !second.IsCompleted,
            "both independent streams must initially wait for exhausted connection credit");

        var updates = new int[3];
        for (var index = 0; index < updates.Length; index++)
        {
            updates[index] = receiver.RecordConsumed(1, 1, 20);
            Ensure(updates[index] == 20,
                "completed consumption must continue producing its immediate original update");
        }
        ApplyProducedUpdates(sender, 1, updates, combine);

        // Credit admission completes the underlying waiter synchronously, but the async
        // cancellation-registration wrapper may still need its scheduled continuation.
        await first.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await second.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(sender.SendConnectionCredit == 0,
            "the two resumed streams must reserve exactly the sixty returned bytes");
        Ensure(sender.ActiveSendStreamCount == 3 && sender.RetainedSendStreamCount == 3,
            "the completed source tombstone must disappear, leaving only the three live streams");
        receiver.AcceptReceived(3, 1, 30);
        receiver.AcceptReceived(4, 1, 30);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegitimateActiveAndFinalUpdatesShouldRestoreTheExactStreamWindow(bool combine)
    {
        var sender = new StreamFlowController(100, 200, 1024);
        var receiver = new StreamFlowController(100, 200, 1024);
        for (var index = 0; index < 5; index++)
            await TransferAsync(sender, receiver, 11, 20);

        var blocked = sender.AcquireSendCreditAsync(11, 1, 100, CancellationToken.None);
        Ensure(!blocked.IsCompleted,
            "connection headroom must not bypass this stream's exhausted window");
        Ensure(receiver.RecordConsumed(11, 1, 20) == 0 &&
               receiver.RecordConsumed(11, 1, 20) == 0,
            "ordinary active consumption must retain its existing below-threshold cadence");
        var thresholdUpdate = receiver.RecordConsumed(11, 1, 20);
        Ensure(thresholdUpdate == 60, "the original stream threshold must still return sixty bytes");
        Ensure(receiver.RecordConsumed(11, 1, 20) == 0 &&
               receiver.RecordConsumed(11, 1, 20) == 0,
            "the remaining active consumption must retain the original batching behavior");
        var finalUpdate = receiver.FlushConsumed(11, 1);
        Ensure(finalUpdate == 40, "EOF must still publish the exact previously unreturned remainder");

        ApplyProducedUpdates(sender, 11, [thresholdUpdate, finalUpdate], combine);

        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(sender.SendConnectionCredit == 100,
            "a full-window reservation after exact repayment must leave exactly one connection window share");
        Ensure(!sender.TryAcquireSendCredit(11, 1, 1),
            "the resumed full-window reservation must not leave phantom stream credit");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CombinedLegitimateUpdatesShouldUnblockAndRepayAnOversizedItem(bool combine)
    {
        var sender = new StreamFlowController(8, 8, 64);
        var receiver = new StreamFlowController(8, 8, 64);
        await TransferAsync(sender, receiver, 21, 2);
        await TransferAsync(sender, receiver, 21, 2);
        sender.CompleteSendStream(21, 1);
        Ensure(receiver.FlushConsumed(21, 1) == 0, "EOF must precede the source's consumption");

        var oversized = sender.AcquireSendCreditAsync(22, 1, 20, CancellationToken.None);
        Ensure(!oversized.IsCompleted,
            "an oversized item may not borrow while any connection debt remains");
        var first = receiver.RecordConsumed(21, 1, 2);
        var second = receiver.RecordConsumed(21, 1, 2);
        Ensure(first == 2 && second == 2, "each completed source item must still produce an update immediately");
        ApplyProducedUpdates(sender, 21, [first, second], combine);

        await oversized.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(sender.SendConnectionCredit == -12,
            "the oversized reservation must carry its exact borrowed connection debt");
        receiver.AcceptReceived(22, 1, 20);
        sender.CompleteSendStream(22, 1);
        Ensure(receiver.FlushConsumed(22, 1) == 0, "the oversized debt must survive EOF until consumption");
        var repayment = receiver.RecordConsumed(22, 1, 20);
        Ensure(repayment == 20, "consuming the oversized item must repay its complete charge");
        sender.ApplyWindowUpdate(22, 1, repayment);
        Ensure(sender.SendConnectionCredit == 8 && sender.RetainedSendStreamCount == 0,
            "oversized repayment must restore the exact negotiated window and release both tombstones");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CombinedFinalUpdatesShouldReleaseBoundedTombstoneCapacity(bool combine)
    {
        var sender = new StreamFlowController(8, 8, 64, maxConcurrentStreams: 1);
        var receiver = new StreamFlowController(8, 8, 64, maxConcurrentStreams: 1);
        for (var index = 0; index < 3; index++)
            await TransferAsync(sender, receiver, 31, 1);
        sender.CompleteSendStream(31, 1);
        Ensure(receiver.FlushConsumed(31, 1) == 0, "completion must retain the partially consumed receive state");

        var replacement = sender.AcquireSendCreditAsync(32, 1, 1, CancellationToken.None);
        Ensure(!replacement.IsCompleted,
            "available connection bytes must not bypass retained receive-state capacity");
        var updates = new int[3];
        for (var index = 0; index < updates.Length; index++)
        {
            updates[index] = receiver.RecordConsumed(31, 1, 1);
            Ensure(updates[index] == 1, "completed single-byte consumption must still publish immediately");
        }
        ApplyProducedUpdates(sender, 31, updates, combine);

        await replacement.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        receiver.AcceptReceived(32, 1, 1);
        Ensure(sender.SendConnectionCredit == 7 && sender.ActiveSendStreamCount == 1 &&
               sender.RetainedSendStreamCount == 1,
            "the replacement must own exactly one byte and the single reused state slot");
        sender.CompleteSendStream(32, 1);
        receiver.FlushConsumed(32, 1);
        sender.ApplyWindowUpdate(32, 1, receiver.RecordConsumed(32, 1, 1));
        Ensure(sender.SendConnectionCredit == 8 && sender.RetainedSendStreamCount == 0,
            "the replacement must also repay and release exactly once");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CombinedLegitimateLateUpdatesAfterAbortMustNotGrantPhantomCredit(bool combine)
    {
        var sender = new StreamFlowController(100, 100, 1024);
        var receiver = new StreamFlowController(100, 100, 1024);
        await TransferAsync(sender, receiver, 41, 20);
        await TransferAsync(sender, receiver, 41, 20);
        receiver.FlushConsumed(41, 1);
        var first = receiver.RecordConsumed(41, 1, 20);
        var second = receiver.RecordConsumed(41, 1, 20);
        Ensure(first == 20 && second == 20, "late updates must represent actual consumed source data");

        var aborted = new SharpLinkException(SharpLinkErrorCode.Cancelled, "source aborted");
        sender.AbortSendStreams(41, aborted);
        Ensure(sender.SendConnectionCredit == 100,
            "abort must refund source debt before its already-produced updates arrive");
        await sender.AcquireSendCreditAsync(42, 1, 100, CancellationToken.None);
        Ensure(sender.SendConnectionCredit == 0, "the replacement must own the entire refunded connection window");

        ApplyProducedUpdates(sender, 41, [first, second], combine);
        Ensure(sender.SendConnectionCredit == 0,
            "late updates for the already-refunded aborted state must not credit the replacement");
        sender.CompleteSendStream(41, 1);
        sender.ApplyWindowUpdate(41, 1, checked(first + second));
        Ensure(sender.SendConnectionCredit == 0 && sender.RetainedSendStreamCount == 1,
            "obsolete updates after aborted-state removal must not recreate state or credit");
        Ensure(!sender.TryAcquireSendCredit(43, 1, 1),
            "no third stream may reserve phantom connection credit from the late updates");
    }

    [Test]
    public async Task CompletedPartialConsumptionMustImmediatelyUnblockDependentOtherStream()
    {
        var sender = new StreamFlowController(100, 100, 1024);
        var receiver = new StreamFlowController(100, 100, 1024);
        await TransferAsync(sender, receiver, 51, 10);
        await TransferAsync(sender, receiver, 51, 10);
        sender.CompleteSendStream(51, 1);
        Ensure(receiver.FlushConsumed(51, 1) == 0, "A must receive EOF before either buffered item is consumed");

        var dependent = sender.AcquireSendCreditAsync(52, 1, 90, CancellationToken.None);
        Ensure(!dependent.IsCompleted && sender.SendConnectionCredit == 80,
            "B's ninety-byte item must initially wait behind A's twenty-byte debt");
        var immediate = receiver.RecordConsumed(51, 1, 10);
        Ensure(immediate == 10,
            "consuming only A's first item must immediately produce credit, not wait for a merge partner");
        sender.ApplyWindowUpdate(51, 1, immediate);
        // Await B before consuming A's second item: no later credit can rescue this wait.
        // The timeout detects a progress failure, not the scheduling speed of the wrapper.
        await dependent.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        receiver.AcceptReceived(52, 1, 90);
        Ensure(sender.SendConnectionCredit == 0,
            "B must reserve exactly the ninety bytes made available by A's first consumption");

        var remaining = receiver.RecordConsumed(51, 1, 10);
        Ensure(remaining == 10, "A's second item must independently publish its final exact credit");
        sender.ApplyWindowUpdate(51, 1, remaining);
        sender.CompleteSendStream(52, 1);
        receiver.FlushConsumed(52, 1);
        sender.ApplyWindowUpdate(52, 1, receiver.RecordConsumed(52, 1, 90));
        Ensure(sender.SendConnectionCredit == 100 && sender.RetainedSendStreamCount == 0,
            "both dependent streams must ultimately restore all credit and release their tombstones");
    }

    private static async ValueTask TransferAsync(
        StreamFlowController sender,
        StreamFlowController receiver,
        long requestId,
        int bytes)
    {
        await sender.AcquireSendCreditAsync(requestId, 1, bytes, CancellationToken.None);
        receiver.AcceptReceived(requestId, 1, bytes);
    }

    private static void ApplyProducedUpdates(
        StreamFlowController sender,
        long requestId,
        int[] updates,
        bool combine)
    {
        var total = 0;
        foreach (var update in updates)
        {
            Ensure(update > 0, "the sum may contain only already-produced positive updates");
            total = checked(total + update);
            if (!combine)
                sender.ApplyWindowUpdate(requestId, 1, update);
        }
        if (combine)
            sender.ApplyWindowUpdate(requestId, 1, total);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
