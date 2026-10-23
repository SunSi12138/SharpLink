using System;
using SharpLink.Runtime;
using TUnit.Core;

namespace SharpLink.UnitTests.Runtime;

// Portable baseline fixture. Keep outside the B2 test project until the independent
// old/current experiment has fixed its case count. No SendCreditLease API is used.
public class SendCreditTerminalAccountingTests
{
    [Test]
    public void ErrorCompletionMustAcceptUnsentRefundWhileFullAbortMustIgnoreItsLateRefund()
    {
        var completion = new StreamFlowController(64, 64, 128, 8);
        var abort = new StreamFlowController(64, 64, 128, 8);
        var original = new InvalidOperationException("selected stream terminal");
        try
        {
            Require(completion.TryAcquireSendCredit(73, 1, 8), "completion arm must reserve eight unsent bytes");
            completion.CompleteSendStream(73, 1, original);
            var completionBeforeRefund = completion.SendConnectionCredit;
            completion.ReturnUnsentCredit(73, 1, 8);
            var completionAfterRefund = completion.SendConnectionCredit;
            var completionRetained = completion.RetainedSendStreamCount;

            Require(abort.TryAcquireSendCredit(73, 1, 8), "abort arm must reserve the same eight bytes");
            abort.AbortSendStreams(73, original);
            var abortBeforeRefund = abort.SendConnectionCredit;
            // The aborted state still exists here. This must exercise the full-abort
            // accounting guard, rather than a benign missing-key return after retirement.
            Require(abort.RetainedSendStreamCount == 1, "the abort control must still have its retained state");
            abort.ReturnUnsentCredit(73, 1, 8);
            var abortAfterRefund = abort.SendConnectionCredit;
            abort.CompleteSendStream(73, 1, original);

            Require(completionBeforeRefund == 56 && completionAfterRefund == 64 && completionRetained == 0 &&
                    abortBeforeRefund == 64 && abortAfterRefund == 64 && abort.RetainedSendStreamCount == 0,
                $"completeBefore={completionBeforeRefund}, completeAfter={completionAfterRefund}, " +
                $"completeRetained={completionRetained}, abortBefore={abortBeforeRefund}, " +
                $"abortAfter={abortAfterRefund}, abortRetained={abort.RetainedSendStreamCount}");
        }
        finally
        {
            completion.Complete(original);
            abort.Complete(original);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Stream terminal accounting assertion failed: {message}.");
    }
}
