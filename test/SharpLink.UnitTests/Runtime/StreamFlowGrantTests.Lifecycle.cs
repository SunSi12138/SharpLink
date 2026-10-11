using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowGrantTests
{
    [Test]
    public async Task DisposeMustRejectOnlyItsUnadmittedWaiterAndKeepTheSameKeyLegacyWaiter()
    {
        var controller = NewGrantController(8, 16, 4);
        Task? owned = null;
        Task? legacy = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 8);
            owned = lease.AcquireAsync(1, CancellationToken.None).AsTask();
            legacy = controller.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None).AsTask();
            GrantEnsure(!owned.IsCompleted && !legacy.IsCompleted
                && controller.CaptureSendGrantSnapshot(1, 0).WaiterCount == 2,
                "both ownership domains must be queued on the same exhausted generation before Dispose");

            lease.Dispose();
            var rejected = false;
            try { await owned.WaitAsync(GrantTestTimeout); }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ConnectionClosed)
            {
                rejected = true;
            }
            GrantEnsure(rejected, "Dispose must reject its own unadmitted exact waiter");
            var remaining = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(!legacy.IsCompleted && remaining.WaiterCount == 1 && remaining.Pressure
                && !remaining.Completed, "context Dispose must not reject or complete a different same-key legacy owner");
            ExpectCredit(controller, 1, 0, 16, 8, 8, 0, 0);

            controller.ApplyWindowUpdate(1, 0, 1);
            await legacy.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 1, 0, 16, 8, 8, 0, 0);
            controller.ReturnUnsentCredit(1, 0, 1);
            controller.ApplyWindowUpdate(1, 0, 7);
            ExpectCredit(controller, 1, 0, 16, 8, 16, 8, 0);
            lease.Complete();
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(owned);
            await ObserveGrantTask(legacy);
        }
    }

    [Test]
    public void LocalGrantConsumptionMustProgressWhileTheConnectionOwnerIsHeld()
    {
        var controller = NewGrantController(32, 128, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 4);
        ReserveLeaseSynchronously(lease, 4);
        var ownerField = typeof(StreamFlowController).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("the production connection authority gate was not found");
        var owner = ownerField.GetValue(controller) as Lock
            ?? throw new InvalidOperationException("the connection authority no longer uses the expected Lock");
        using var finished = new ManualResetEventSlim();
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try { ReserveLeaseSynchronously(lease, 1); }
            catch (Exception error) { workerError = error; }
            finally { finished.Set(); }
        })
        { IsBackground = true };

        bool progressed;
        lock (owner)
        {
            worker.Start();
            progressed = finished.Wait(GrantTestTimeout);
        }
        var joined = worker.Join(GrantTestTimeout);
        GrantEnsure(progressed && joined,
            "an existing local grant must complete without acquiring the held connection owner gate");
        GrantEnsure(workerError is null, $"the local worker failed: {workerError}");
        ExpectCredit(controller, 1, 0, 128, 32, 108, 23, 11);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetainedProducerContextMustRejectCrossThreadWorkAfterThePoolObjectIsReused(bool sameKey)
    {
        var controller = NewGrantController(32, 128, 16, maxStreams: 1);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? producer = null;
        try
        {
            var old = CaptureFirst(controller, 1, 0, 4);
            ReserveLeaseSynchronously(old, 4);
            var original = controller.CaptureSendGrantSnapshot(1, 0);
            producer = Task.Run(async () =>
            {
                captured.TrySetResult();
                await release.Task;
                try { await old.AcquireAsync(1, CancellationToken.None); }
                finally
                {
                    // The old helper's original unsent item was already settled by Abort.
                    old.ReturnUnsent(4);
                    old.Dispose();
                    old.Complete();
                }
            });
            await captured.Task.WaitAsync(GrantTestTimeout);
            var selected = new OperationCanceledException("cross-thread original stream cancel");
            controller.AbortSendStreams(1, selected);
            old.Complete(selected);
            var replacementKey = sameKey ? 1L : 2L;
            var replacement = CaptureFirst(controller, replacementKey, 0, 2);
            ReserveLeaseSynchronously(replacement, 2);
            var current = controller.CaptureSendGrantSnapshot(replacementKey, 0);
            GrantEnsure(ReferenceEquals(original.StateIdentity, current.StateIdentity)
                && original.Lease != current.Lease,
                "the blocked producer must resume only after the exact object has a different generation");
            release.TrySetResult();
            await ExpectGrantException(producer, selected);
            ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 28, 14);
            GrantEnsure(!controller.CaptureSendGrantSnapshot(replacementKey, 0).LeaseClosed,
                "an old producer's finally must not close the current generation");
        }
        finally
        {
            release.TrySetResult();
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(producer);
        }
    }

    [Test]
    public async Task AlreadyAdmittedItemMustRemainOwnedWhenDisposeAndCancellationArriveBeforeAwait()
    {
        var controller = NewGrantController(8, 16, 4);
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 8);
            pending = lease.AcquireAsync(2, cancellation.Token).AsTask();
            GrantEnsure(!pending.IsCompleted, "the second item must initially wait for stream credit");
            controller.ApplyWindowUpdate(1, 0, 2);
            var admitted = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(admitted.WaiterCount == 0 && admitted.StreamCredit == 0
                && admitted.ConnectionCredit == 8 && admitted.TotalDebt == 8,
                "the owner must have admitted exact n before the caller observes the ValueTask result");

            // The application has not awaited the successful item yet. Cancellation cannot revoke
            // a reservation already removed from the waiter list or replace its owner with Dispose.
            lease.Dispose();
            cancellation.Cancel();
            ExpectCredit(controller, 1, 0, 16, 8, 8, 0, 0);
            await pending.WaitAsync(GrantTestTimeout);
            lease.ReturnUnsent(2);
            ExpectCredit(controller, 1, 0, 16, 8, 10, 2, 0);
            controller.ApplyWindowUpdate(1, 0, 6);
            ExpectCredit(controller, 1, 0, 16, 8, 16, 8, 0);
            lease.Complete();
            GrantEnsure(controller.RetainedSendStreamCount == 0,
                "after the application-owned item and prior debt settle there must be no retained state");
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(pending);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public void RetiredLeaseActionsMustNotModifyAReplacementGeneration(bool sameKey, bool abort)
    {
        var controller = NewGrantController(32, 128, 16, maxStreams: 1);
        using var cleanup = new GrantControllerCleanup(controller);
        var old = CaptureFirst(controller, 1, 0, 4);
        ReserveLeaseSynchronously(old, 4);
        var oldSnapshot = controller.CaptureSendGrantSnapshot(1, 0);
        var selected = new OperationCanceledException("original generation remote cancel");
        if (abort)
        {
            controller.AbortSendStreams(1, selected);
            old.Complete(selected);
        }
        else
        {
            old.Complete();
            controller.ApplyWindowUpdate(1, 0, 8);
        }
        GrantEnsure(controller.RetainedSendStreamCount == 0,
            "the original state must actually retire before exercising the stale context");

        var replacementKey = sameKey ? 1L : 2L;
        var replacement = CaptureFirst(controller, replacementKey, 0, 2);
        ReserveLeaseSynchronously(replacement, 2);
        var nextSnapshot = controller.CaptureSendGrantSnapshot(replacementKey, 0);
        GrantEnsure(ReferenceEquals(oldSnapshot.StateIdentity, nextSnapshot.StateIdentity),
            "the test must use the exact same pooled state object, not only a different dictionary entry");
        GrantEnsure(nextSnapshot.Lease != oldSnapshot.Lease,
            "the reused object must have a new immutable generation");
        ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 28, 14);

        old.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stale late complete"));
        ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 28, 14);
        old.ReturnUnsent(4);
        ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 28, 14);
        old.DisableGrant();
        old.Dispose();
        ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 28, 14);
        var after = controller.CaptureSendGrantSnapshot(replacementKey, 0);
        GrantEnsure(!after.Completed && !after.LeaseClosed && after.GrantEnabled
            && ReferenceEquals(after.CurrentLease, replacement),
            "old cleanup must not terminate or take ownership of the replacement's context");
        if (abort)
            ExpectGrantException(() => old.TryAcquire(1, CancellationToken.None), selected);
        else
            ExpectClosedGrant(() => old.TryAcquire(1, CancellationToken.None));
        ReserveLeaseSynchronously(replacement, 1);
        ExpectCredit(controller, replacementKey, 0, 128, 32, 110, 27, 13);
    }

    [Test]
    public async Task StreamAbortMustPreserveTheOriginalExceptionAcrossWaiterAndPoolRetirement()
    {
        var controller = NewGrantController(16, 32, 8);
        Task? pending = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 4);
            ReserveLeaseSynchronously(lease, 4);
            pending = lease.AcquireAsync(12, CancellationToken.None).AsTask();
            GrantEnsure(!pending.IsCompleted, "the request must wait for stream rather than connection credit");
            var selected = new SharpLinkException(SharpLinkErrorCode.Cancelled, "the original remote cancel reason");
            controller.AbortSendStreams(1, selected);
            await ExpectGrantException(pending, selected);
            ExpectCredit(controller, 1, 0, 32, 16, 32, 16, 0);
            GrantEnsure(controller.CaptureSendGrantSnapshot(1, 0).LeaseClosed,
                "Abort must prevent new local consumption before exposing returned connection credit");
            controller.ApplyWindowUpdate(1, 0, int.MaxValue);
            lease.ReturnUnsent(8);
            ExpectCredit(controller, 1, 0, 32, 16, 32, 16, 0);
            ExpectGrantException(() => lease.TryAcquire(1, CancellationToken.None), selected);
            lease.Complete();
            var next = CaptureFirst(controller, 2, 0, 1);
            ExpectGrantException(() => lease.TryAcquire(1, CancellationToken.None), selected);
            next.ReturnUnsent(1);
            next.Complete();
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(pending);
        }
    }

    [Test]
    public async Task ConnectionCloseMustRejectLeasesAndWaitersWithTheSelectedError()
    {
        var controller = NewGrantController(16, 32, 8);
        Task? pending = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 4);
            ReserveLeaseSynchronously(lease, 4);
            GrantEnsure(controller.TryAcquireSendCredit(2, 0, 16), "the second stream must hold sixteen bytes");
            pending = controller.AcquireSendCreditAsync(3, 0, 16, CancellationToken.None).AsTask();
            GrantEnsure(!pending.IsCompleted, "the third stream must wait behind real connection debt");
            var selected = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "selected connection teardown");
            controller.Complete(selected);
            await ExpectGrantException(pending, selected);
            ExpectGrantException(() => lease.TryAcquire(1, CancellationToken.None), selected);
            lease.ReturnUnsent(8);
            lease.DisableGrant();
            lease.Dispose();
            lease.Complete();
            var final = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(!final.HasState && final.RetainedStreams == 0 && final.ActiveStreams == 0
                && final.TotalUnusedGrant == 0 && final.WaiterCount == 0,
                "late lease cleanup after connection teardown must not recreate state, grants, or waiters");
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(pending);
        }
    }

    [Test]
    public async Task CancelBeforeAdmissionMustRemovePressureWithoutSpendingTheItem()
    {
        var controller = NewGrantController(16, 32, 8);
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 8);
            ReserveLeaseSynchronously(lease, 8);
            pending = lease.AcquireAsync(1, cancellation.Token).AsTask();
            GrantEnsure(!pending.IsCompleted, "the stream is genuinely exhausted before cancellation");
            cancellation.Cancel();
            await ExpectGrantCancellation(pending, cancellation.Token);
            ExpectCredit(controller, 1, 0, 32, 16, 16, 0, 0);
            var canceled = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(canceled.WaiterCount == 0 && !canceled.Pressure,
                "cancel-before-admission must neither spend the requested byte nor strand queue pressure");
            controller.ApplyWindowUpdate(1, 0, 1);
            GrantEnsure(lease.TryAcquire(1, CancellationToken.None), "real returned credit must still permit progress");
            ExpectCredit(controller, 1, 0, 32, 16, 16, 0, 0);
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(pending);
        }
    }
}
