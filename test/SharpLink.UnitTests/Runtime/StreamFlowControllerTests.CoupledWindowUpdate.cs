using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

// Regression for dev's original two-independent-clamps ApplyWindowUpdate behavior.
// These checks use only the production keyed API. No resolved-handle or B3 code is linked.
public partial class StreamFlowControllerTests
{
    [Test]
    public void LateUpdateAfterAbortMustNotFundAnUnrelatedStream()
    {
        var sender = NewCreditConservationController(4, 4);
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            sender.AbortSendStreams(1, new OperationCanceledException("A aborted"));
            CreditConservationReserve(sender, 2, 0, 4);
            sender.ApplyWindowUpdate(1, 0, 4);

            CreditConservationEnsure(sender.SendConnectionCredit == 0,
                "old A credit was already refunded by Abort; B still owns the connection window");
            CreditConservationEnsure(!sender.TryAcquireSendCredit(3, 0, 1),
                "a delayed A WindowUpdate minted credit for unrelated stream C");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    [Arguments(1, 3)]
    [Arguments(2, 4)]
    [Arguments(3, 4)]
    [Arguments(int.MaxValue, 4)]
    public void CreditMustEqualEffectiveStreamDelta(int update, int expectedConnectionCredit)
    {
        var sender = NewCreditConservationController(4, 8);
        try
        {
            CreditConservationReserve(sender, 1, 0, 2);
            CreditConservationReserve(sender, 2, 0, 4);
            sender.ApplyWindowUpdate(1, 0, update);
            CreditConservationEnsure(sender.SendConnectionCredit == expectedConnectionCredit,
                "connection credit increased beyond the amount actually restored to A");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public void OversizedBorrowMustNotRefundMoreThanTheActualDebt()
    {
        var sender = NewCreditConservationController(2, 8);
        try
        {
            CreditConservationReserve(sender, 1, 0, 6); // A: 2 -> -4.
            CreditConservationReserve(sender, 2, 0, 2);
            sender.ApplyWindowUpdate(1, 0, 7);
            CreditConservationEnsure(sender.SendConnectionCredit == 6,
                "oversized A owes six bytes, not seven");
            sender.ReturnUnsentCredit(2, 0, 2);
            CreditConservationEnsure(sender.SendConnectionCredit == 8,
                "one legitimate B refund must restore exactly the connection window");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public void NormalUnsentRefundMustSucceedAfterAnotherStreamsLateUpdate()
    {
        var sender = NewCreditConservationController(4, 4);
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            sender.AbortSendStreams(1, new OperationCanceledException("A aborted"));
            CreditConservationReserve(sender, 2, 0, 4);
            sender.ApplyWindowUpdate(1, 0, 4);
            sender.ReturnUnsentCredit(2, 0, 4);
            CreditConservationEnsure(sender.SendConnectionCredit == 4,
                "B's first unsent return was not a double refund");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public async Task LateAbortUpdateMustNotWakeAConnectionWaiter()
    {
        var sender = NewCreditConservationController(4, 4);
        Task? waiterB = null;
        Task? waiterC = null;
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            waiterB = sender.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None).AsTask();
            sender.AbortSendStreams(1, new OperationCanceledException("A aborted"));
            await waiterB.WaitAsync(TimeSpan.FromSeconds(10));

            waiterC = sender.AcquireSendCreditAsync(3, 0, 1, CancellationToken.None).AsTask();
            CreditConservationEnsure(!waiterC.IsCompleted, "C must initially wait behind B");
            sender.ApplyWindowUpdate(1, 0, 4);
            CreditConservationEnsure(sender.SendConnectionCredit == 0 && !waiterC.IsCompleted,
                "a duplicate A refund admitted a connection-credit waiter");
            sender.ApplyWindowUpdate(2, 0, 1);
            await waiterC.WaitAsync(TimeSpan.FromSeconds(10));
            CreditConservationEnsure(sender.SendConnectionCredit == 0,
                "C may consume only B's genuine returned byte");
        }
        finally
        {
            sender.Complete(new OperationCanceledException("test cleanup"));
            await CreditConservationObserve(waiterB);
            await CreditConservationObserve(waiterC);
        }
    }

    [Test]
    public void CompletedTombstoneMustRejectExcessCredit()
    {
        var sender = NewCreditConservationController(4, 8);
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            sender.CompleteSendStream(1, 0);
            CreditConservationReserve(sender, 2, 0, 4);
            sender.ApplyWindowUpdate(1, 0, 6);
            CreditConservationEnsure(sender.SendConnectionCredit == 4,
                "a completed A can only repay its actual four-byte debt");
            sender.ApplyWindowUpdate(1, 0, int.MaxValue);
            CreditConservationEnsure(sender.SendConnectionCredit == 4,
                "credit for a removed tombstone must be discarded");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public void UnknownOrRemovedStreamUpdateMustRemainNoOp()
    {
        var sender = NewCreditConservationController(4, 4);
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            sender.CompleteSendStream(1, 0);
            sender.ApplyWindowUpdate(1, 0, 4);
            CreditConservationReserve(sender, 2, 0, 4);
            sender.ApplyWindowUpdate(1, 0, int.MaxValue);
            sender.ApplyWindowUpdate(1234, 7, 3);
            CreditConservationEnsure(sender.SendConnectionCredit == 0,
                "an obsolete/missing stream must not create a state or new credit");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public void NamedAndDefaultSendStreamsMustNotDoubleRefundAnAbortedRequest()
    {
        var sender = NewCreditConservationController(4, 8);
        try
        {
            CreditConservationReserve(sender, 1, 0, 4);
            CreditConservationReserve(sender, 1, 7, 4);
            sender.AbortSendStreams(1, new OperationCanceledException("request 1 aborted"));
            CreditConservationReserve(sender, 2, 0, 4);
            CreditConservationReserve(sender, 2, 7, 4);
            sender.ApplyWindowUpdate(1, 0, 4);
            sender.ApplyWindowUpdate(1, 7, 4);
            CreditConservationEnsure(sender.SendConnectionCredit == 0,
                "late default/named updates refunded request 1 a second time");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    [Test]
    public void NegativeConnectionCreditMustRepayBorrowPrecisely()
    {
        var sender = NewCreditConservationController(2, 4);
        try
        {
            CreditConservationReserve(sender, 1, 0, 6);
            CreditConservationEnsure(sender.SendConnectionCredit == -2,
                "the oversized-item borrow semantics changed");
            sender.ApplyWindowUpdate(1, 0, 1);
            CreditConservationEnsure(sender.SendConnectionCredit == -1,
                "partial borrow repayment must remain negative");
            sender.ApplyWindowUpdate(1, 0, 5);
            CreditConservationEnsure(sender.SendConnectionCredit == 4,
                "full borrow repayment must restore the exact window");
        }
        finally { sender.Complete(new OperationCanceledException("test cleanup")); }
    }

    private static StreamFlowController NewCreditConservationController(int stream, int connection)
        => new(stream, connection, maxFramePayloadBytes: 16, maxConcurrentStreams: 8);

    private static void CreditConservationReserve(
        StreamFlowController sender, long requestId, ushort streamId, int bytes)
    {
        CreditConservationEnsure(sender.TryAcquireSendCredit(requestId, streamId, bytes),
            "the test's initial reservation must succeed synchronously");
    }

    private static async Task CreditConservationObserve(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is not TimeoutException) { }
    }

    private static void CreditConservationEnsure(bool okay, string reason)
    {
        if (!okay) throw new InvalidOperationException(reason);
    }
}
