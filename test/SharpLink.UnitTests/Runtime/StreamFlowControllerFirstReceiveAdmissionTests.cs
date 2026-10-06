using System;
using System.Linq;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

#if !SHARPLINK_FIRST_RECEIVE_EVIDENCE
public sealed class StreamFlowControllerFirstReceiveAdmissionTests
{
    [Test] public void FirstDebitCapturesFullIdentity() => FirstReceiveAdmissionChecks.FirstDebit();
    [Test] public void RejectedFirstReceiveMustNotConsumeStateCapacity() => FirstReceiveAdmissionChecks.RejectedFirst();
    [Test] public void FirstAdmissionPreservesExistingStreamWindow() => FirstReceiveAdmissionChecks.ExistingWindow();
    [Test] public void FirstAdmissionPreservesCompletedTombstone() => FirstReceiveAdmissionChecks.Tombstone();
    [Test] public void InvalidFirstFrameMustNotPublishState() => FirstReceiveAdmissionChecks.InvalidFrame();
    [Test] public void FirstAdmissionPreservesTerminalExceptionIdentity() => FirstReceiveAdmissionChecks.Terminal();
    [Test] public async Task ConcurrentFirstFramesShareOneGeneration() => await FirstReceiveAdmissionChecks.ConcurrentFirstAsync();
    [Test] public void FirstAdmissionLeaseRejectsForeignController() => FirstReceiveAdmissionChecks.ForeignOwner();
    [Test] public void FirstReceiveStateReusesOneHundredThousandGenerations() => FirstReceiveAdmissionChecks.Reuse();
}
#endif

internal static class FirstReceiveAdmissionChecks
{
    internal static async Task RunAllAsync()
    {
        FirstDebit();
        RejectedFirst();
        ExistingWindow();
        Tombstone();
        InvalidFrame();
        Terminal();
        await ConcurrentFirstAsync();
        ForeignOwner();
        Reuse();
        Console.WriteLine("9/9 first-receive admission checks passed.");
    }

    internal static void FirstDebit()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var lease = controller.AcceptReceivedCreditLease(long.MinValue, ushort.MaxValue, 4);
        Require(ReferenceEquals(lease.Owner, controller) && lease.State is not null &&
                lease.Generation > 0 && lease.RequestId == long.MinValue && lease.StreamId == ushort.MaxValue,
            "first debit must capture the full owner/state/generation/key identity");
        Repay(controller, lease, 4);
        var next = controller.AcceptReceivedCreditLease(long.MaxValue, 0, 16);
        // Oversized first DATA may borrow only while both windows are fully restored.
        Require(Reject(() => controller.AcceptReceivedCreditLease(long.MaxValue, 0, 1)) is SharpLinkException,
            "oversized debt must block later admission");
        Repay(controller, next, 16);
    }

    internal static void RejectedFirst()
    {
        var controller = new StreamFlowController(8, 8, 1024, 2);
        var first = controller.AcceptReceivedCreditLease(1, 1, 8);
        Require(Reject(() => controller.AcceptReceivedCreditLease(2, 1, 1)) is SharpLinkException,
            "a first frame must respect exhausted connection credit");
        Require(controller.RecordConsumed(in first, 8) == 8, "first stream must recover its real debit");
        // Keep first active: a rejected key 2 left in the dictionary would fill both slots.
        var third = controller.AcceptReceivedCreditLease(3, 1, 8);
        Repay(controller, third, 8);
        _ = controller.FlushConsumed(in first);
    }

    internal static void ExistingWindow()
    {
        var controller = new StreamFlowController(8, 16, 1024, 2);
        var first = controller.AcceptReceivedCreditLease(4, 1, 8);
        Require(Reject(() => controller.AcceptReceivedCreditLease(4, 1, 1)) is SharpLinkException,
            "existing stream exhaustion must not bypass the stream hard bound");
        var independent = controller.AcceptReceivedCreditLease(5, 1, 8);
        Require(controller.RecordConsumed(in first, 8) == 8, "failed admission cannot change first debt");
        Repay(controller, independent, 8);
        var same = controller.AcceptReceivedCreditLease(4, 1, 8);
        Require(ReferenceEquals(first.State, same.State) && first.Generation == same.Generation,
            "an existing live stream must retain its generation");
        Repay(controller, same, 8);
    }

    internal static void Tombstone()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var old = controller.AcceptReceivedCreditLease(6, 1, 8);
        Require(controller.FlushConsumed(in old) == 0, "unconsumed terminal has no invented credit");
        Require(Reject(() => controller.AcceptReceivedCreditLease(6, 1, 1)) is SharpLinkException,
            "completed receive tombstones cannot accept first DATA");
        Require(controller.RecordConsumed(in old, 8) == 8, "late consumption retires the tombstone");
        var next = controller.AcceptReceivedCreditLease(6, 1, 8);
        Require(ReferenceEquals(old.State, next.State) && old.Generation != next.Generation,
            "same-key admission must capture the new pooled-state generation");
        Require(controller.RecordConsumed(in old, 1) == 0 && controller.FlushConsumed(in old) == 0,
            "old handles cannot refund or terminate the replacement");
        Require(Reject(() => controller.AcceptReceived(in old, 1)) is SharpLinkException,
            "old handles cannot debit the replacement");
        Repay(controller, next, 8);
    }

    internal static void InvalidFrame()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        Require(Reject(() => controller.AcceptReceivedCreditLease(7, 1, 0)) is ArgumentOutOfRangeException,
            "zero-byte encoded frame must be rejected");
        Require(Reject(() => controller.AcceptReceivedCreditLease(7, 1, -1)) is ArgumentOutOfRangeException,
            "negative-byte encoded frame must be rejected");
        Require(Reject(() => controller.AcceptReceivedCreditLease(7, 1, 1023)) is SharpLinkException,
            "frame payload bound must be preserved");
        var valid = controller.AcceptReceivedCreditLease(8, 1, 8);
        Repay(controller, valid, 8);
    }

    internal static void Terminal()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var expected = new InvalidOperationException("terminal control");
        controller.Complete(expected);
        Require(ReferenceEquals(Reject(() => controller.AcceptReceivedCreditLease(9, 1, 1)), expected),
            "the terminal exception must retain exact identity");
    }

    internal static async Task ConcurrentFirstAsync()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            return controller.AcceptReceivedCreditLease(10, 1, 4);
        }).ToArray();
        start.SetResult();
        var leases = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Require(ReferenceEquals(leases[0].State, leases[1].State) && leases[0].Generation == leases[1].Generation,
            "concurrent first frames must join the same live generation");
        Require(Reject(() => controller.AcceptReceived(in leases[0], 1)) is SharpLinkException,
            "concurrent admission must debit both frames exactly once");
        var returned = controller.RecordConsumed(in leases[0], 4) + controller.RecordConsumed(in leases[1], 4);
        returned += controller.FlushConsumed(in leases[0]);
        Require(returned == 8, "concurrent first-frame credit must finish balanced");
    }

    internal static void ForeignOwner()
    {
        var first = new StreamFlowController(8, 8, 1024, 1);
        var second = new StreamFlowController(8, 8, 1024, 1);
        var original = first.AcceptReceivedCreditLease(11, 1, 8);
        var other = second.AcceptReceivedCreditLease(11, 1, 8);
        Require(original.Generation == other.Generation, "foreign owner control needs equal numeric generations");
        Require(Reject(() => second.AcceptReceived(in original, 1)) is SharpLinkException,
            "an equal key/generation cannot replace controller identity");
        Require(second.RecordConsumed(in original, 8) == 0, "foreign handles cannot return credit");
        Repay(first, original, 8);
        Repay(second, other, 8);
    }

    internal static void Reuse()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var previous = default(StreamFlowController.ResolvedReceiveCreditLease);
        for (var index = 0; index < 100000; index++)
        {
            var current = controller.AcceptReceivedCreditLease(index + 100L, 1, 8);
            if (previous.IsResolved)
            {
                Require(ReferenceEquals(previous.State, current.State) && previous.Generation != current.Generation,
                    "every pooled-state rent must capture a new generation");
                Require(controller.RecordConsumed(in previous, 1) == 0 && controller.FlushConsumed(in previous) == 0,
                    "previous generations cannot settle new DATA");
            }
            Repay(controller, current, 8);
            previous = current;
        }
    }

    private static void Repay(StreamFlowController controller,
        StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
    {
        var returned = controller.RecordConsumed(in lease, bytes) + controller.FlushConsumed(in lease);
        Require(returned == bytes, "first admission, consumption and terminal must conserve all credit");
    }

    private static Exception Reject(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("The invalid admission unexpectedly succeeded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
