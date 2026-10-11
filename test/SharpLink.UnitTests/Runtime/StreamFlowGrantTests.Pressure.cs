using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowGrantTests
{
    [Test]
    public async Task OwnerMustRecallIdleGrantsWithoutSchedulingTheirProducers()
    {
        var controller = NewGrantController(32, 32, 16);
        Task? waiting = null;
        try
        {
            var first = CaptureFirst(controller, 1, 0, 1);
            ReserveLeaseSynchronously(first, 1);
            var second = CaptureFirst(controller, 2, 0, 1);
            ReserveLeaseSynchronously(second, 1);
            var before = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(before.ConnectionCredit == 0 && before.TotalUnusedGrant == 28 && before.TotalDebt == 4,
                "two idle producers must hold all otherwise available connection credit as unused grants");

            // Neither first nor second runs again: only the new claimant invokes the authority.
            waiting = controller.AcquireSendCreditAsync(3, 0, 28, CancellationToken.None).AsTask();
            var after = controller.CaptureSendGrantSnapshot(3, 0);
            GrantEnsure(after.TotalUnusedGrant == 0 && after.WaiterCount == 0,
                "the owner must recall idle credit and admit a legal exact item without producer cooperation");
            await waiting.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 3, 0, 32, 32, 0, 4, 0);
            GrantEnsure(controller.CaptureSendGrantSnapshot(1, 0).StreamCredit == 30
                && controller.CaptureSendGrantSnapshot(2, 0).StreamCredit == 30,
                "recall must not manufacture new stream debt for the idle producers");
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(waiting);
        }
    }

    [Test]
    public async Task ConnectionHeadPressureMustPreventLocalBypassAndCancelMustUnblockNext()
    {
        var controller = NewGrantController(32, 32, 16);
        using var cancellation = new CancellationTokenSource();
        Task? head = null;
        Task? behind = null;
        try
        {
            var first = CaptureFirst(controller, 1, 0, 1);
            ReserveLeaseSynchronously(first, 1);
            var second = CaptureFirst(controller, 2, 0, 1);
            ReserveLeaseSynchronously(second, 1);
            head = controller.AcquireSendCreditAsync(3, 0, 31, cancellation.Token).AsTask();
            var pressured = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(pressured.Pressure && pressured.WaiterCount == 1
                && pressured.ConnectionCredit == 28 && pressured.TotalUnusedGrant == 0,
                "a connection-blocked head must freeze and recall grants before ordered admission");

            behind = first.AcquireAsync(1, CancellationToken.None).AsTask();
            GrantEnsure(!head.IsCompleted && !behind.IsCompleted,
                "a smaller local item must not bypass an existing connection-credit-blocked head");
            GrantEnsure(controller.CaptureSendGrantSnapshot(1, 0).WaiterCount == 2,
                "the local miss must use the existing ordered exact waiter path");
            cancellation.Cancel();
            await ExpectGrantCancellation(head, cancellation.Token);
            await behind.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 1, 0, 32, 32, 27, 29, 0);
            var unblocked = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(!unblocked.Pressure && unblocked.WaiterCount == 0,
                "canceling the head must clear empty-queue pressure and preserve wakeups");
            ReserveLeaseSynchronously(first, 1);
            ExpectCredit(controller, 1, 0, 32, 32, 11, 28, 15);
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(head);
            await ObserveGrantTask(behind);
        }
    }

    [Test]
    public async Task StreamCreditBlockedHeadMustStillAllowAnIndependentExactItem()
    {
        var controller = NewGrantController(8, 32, 4);
        using var cancellation = new CancellationTokenSource();
        Task? head = null;
        Task? other = null;
        try
        {
            var first = CaptureFirst(controller, 1, 0, 8);
            head = first.AcquireAsync(1, cancellation.Token).AsTask();
            other = controller.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None).AsTask();
            await other.WaitAsync(GrantTestTimeout);
            GrantEnsure(!head.IsCompleted, "the exhausted stream must still wait for its own WindowUpdate");
            ExpectCredit(controller, 2, 0, 32, 8, 20, 4, 0);
            cancellation.Cancel();
            await ExpectGrantCancellation(head, cancellation.Token);
            var final = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(!final.Pressure && final.WaiterCount == 0,
                "the stream-blocked cancellation must remove its pressure without disturbing the other item");
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(head);
            await ObserveGrantTask(other);
        }
    }

    [Test]
    public async Task OversizedBorrowMustRecallIdleGrantAndWaitForItsCompleteRepayment()
    {
        var controller = NewGrantController(8, 16, 8);
        Task? firstBorrow = null;
        Task? secondBorrow = null;
        try
        {
            var idle = CaptureFirst(controller, 1, 0, 1);
            ReserveLeaseSynchronously(idle, 1);
            controller.ApplyWindowUpdate(1, 0, 2);
            ExpectCredit(controller, 1, 0, 16, 8, 10, 8, 6);
            firstBorrow = controller.AcquireSendCreditAsync(2, 0, 20, CancellationToken.None).AsTask();
            GrantEnsure(controller.CaptureSendGrantSnapshot(2, 0).TotalUnusedGrant == 0,
                "full-window oversized eligibility must actively recover all idle connection grant");
            await firstBorrow.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 2, 0, 16, 8, -4, -12, 0);

            secondBorrow = controller.AcquireSendCreditAsync(2, 0, 20, CancellationToken.None).AsTask();
            controller.ApplyWindowUpdate(2, 0, 1);
            ExpectCredit(controller, 2, 0, 16, 8, -3, -11, 0);
            GrantEnsure(!secondBorrow.IsCompleted, "partial repayment cannot authorize a second oversized item");
            controller.ApplyWindowUpdate(2, 0, 18);
            ExpectCredit(controller, 2, 0, 16, 8, 15, 7, 0);
            GrantEnsure(!secondBorrow.IsCompleted, "nineteen of twenty bytes still leaves a real debt");
            controller.ApplyWindowUpdate(2, 0, 1);
            await secondBorrow.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 2, 0, 16, 8, -4, -12, 0);
            controller.ReturnUnsentCredit(2, 0, 20);
            ExpectCredit(controller, 2, 0, 16, 8, 16, 8, 0);
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(firstBorrow);
            await ObserveGrantTask(secondBorrow);
        }
    }

    [Test]
    public void ExcessWindowUpdateWithUnusedGrantMustCreditOnlyActualStreamDebt()
    {
        var controller = NewGrantController(16, 64, 8);
        using var cleanup = new GrantControllerCleanup(controller);
        var first = CaptureFirst(controller, 1, 0, 2);
        ReserveLeaseSynchronously(first, 2);
        GrantEnsure(controller.TryAcquireSendCredit(2, 0, 16), "the unrelated stream must own sixteen bytes");
        ExpectCredit(controller, 1, 0, 64, 16, 38, 12, 6);
        controller.ApplyWindowUpdate(1, 0, 1);
        ExpectCredit(controller, 1, 0, 64, 16, 39, 13, 6);
        controller.ApplyWindowUpdate(1, 0, int.MaxValue);
        ExpectCredit(controller, 1, 0, 64, 16, 42, 16, 6);
        controller.ApplyWindowUpdate(1, 0, 64);
        ExpectCredit(controller, 1, 0, 64, 16, 42, 16, 6);
        first.Dispose();
        controller.ReturnUnsentCredit(2, 0, 16);
        ExpectCredit(controller, 1, 0, 64, 16, 64, 16, 0);
    }

    [Test]
    public void NamedStreamAbortMustSettleBothGrantsOnceAndIgnoreTheirLateUpdates()
    {
        var controller = NewGrantController(16, 64, 8);
        using var cleanup = new GrantControllerCleanup(controller);
        var unnamed = CaptureFirst(controller, 10, 0, 2);
        ReserveLeaseSynchronously(unnamed, 2);
        var named = CaptureFirst(controller, 10, 7, 2);
        ReserveLeaseSynchronously(named, 2);
        var before = controller.CaptureSendGrantSnapshot(10, 0);
        GrantEnsure(before.ConnectionCredit == 44 && before.TotalUnusedGrant == 12 && before.TotalDebt == 8,
            "named streams must each own distinct grant and exact debt");
        var error = new OperationCanceledException("remote named request cancel");
        controller.AbortSendStreams(10, error);
        ExpectCredit(controller, 10, 0, 64, 16, 64, 16, 0);
        ExpectCredit(controller, 10, 7, 64, 16, 64, 16, 0);
        GrantEnsure(controller.TryAcquireSendCredit(11, 0, 32), "a new request may use the legitimately restored window");
        controller.ApplyWindowUpdate(10, 0, 100);
        controller.ApplyWindowUpdate(10, 7, int.MaxValue);
        unnamed.ReturnUnsent(4);
        named.ReturnUnsent(4);
        ExpectCredit(controller, 11, 0, 64, 16, 32, -16, 0);
        controller.ReturnUnsentCredit(11, 0, 32);
        GrantEnsure(controller.SendConnectionCredit == 64, "late cancel cleanup must not create phantom credit");
    }
}
