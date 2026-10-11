using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

// These exercise the production B2 credit API. They are not a transport harness or a cost model.
public partial class StreamFlowGrantTests
{
    [Test]
    public void FirstCaptureMustReserveOnlyTheCurrentItemAndRejectASecondContext()
    {
        var controller = NewGrantController(32, 128, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 3);
        ExpectCredit(controller, 1, 0, 128, 32, 125, 29, 0);

        var accepted = controller.TryAcquireSendCreditLease(
            1, 0, 7, CancellationToken.None, out var second);
        GrantEnsure(!accepted && second is null,
            "a second capture must not share the first context's Dispose ownership or reserve seven bytes");
        var snapshot = controller.CaptureSendGrantSnapshot(1, 0);
        GrantEnsure(ReferenceEquals(snapshot.CurrentLease, lease), "the first context must remain authoritative");
        ExpectCredit(controller, 1, 0, 128, 32, 125, 29, 0);

        lease.Complete();
        lease.ReturnUnsent(3);
        GrantEnsure(controller.RetainedSendStreamCount == 0 && controller.SendConnectionCredit == 128,
            "the current helper may refund its unsent first item after normal Complete");
    }

    [Test]
    [Arguments(256)]
    [Arguments(1024)]
    [Arguments(4096)]
    public void RefillQuantumIncludesTheCurrentItemAndLocalConsumptionPreservesF(int quantum)
    {
        const int streamWindow = 8192;
        const int connectionWindow = 32768;
        var controller = NewGrantController(streamWindow, connectionWindow, quantum);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 1);

        ReserveLeaseSynchronously(lease, 1);
        ExpectCredit(controller, 1, 0, connectionWindow, streamWindow,
            connectionWindow - 1 - quantum, streamWindow - 2, quantum - 1);
        ReserveLeaseSynchronously(lease, 1);
        ExpectCredit(controller, 1, 0, connectionWindow, streamWindow,
            connectionWindow - 1 - quantum, streamWindow - 3, quantum - 2);

        lease.DisableGrant();
        ExpectCredit(controller, 1, 0, connectionWindow, streamWindow,
            connectionWindow - 3, streamWindow - 3, 0);
        lease.ReturnUnsent(3);
        lease.Complete();
        GrantEnsure(controller.SendConnectionCredit == connectionWindow,
            "three actual item reservations, rather than a quantum of stream debt, must settle");
    }

    [Test]
    [Arguments(256)]
    [Arguments(1024)]
    [Arguments(4096)]
    public void ItemLargerThanQuantumUsesExactCreditWithoutUnusedGrant(int quantum)
    {
        const int streamWindow = 16384;
        const int connectionWindow = 32768;
        var controller = NewGrantController(streamWindow, connectionWindow, quantum);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 1);
        ReserveLeaseSynchronously(lease, quantum + 1);
        ExpectCredit(controller, 1, 0, connectionWindow, streamWindow,
            connectionWindow - quantum - 2, streamWindow - quantum - 2, 0);
    }

    [Test]
    public void RefillMustNotReserveMoreGrantThanTheStreamsRemainingCredit()
    {
        var controller = NewGrantController(8, 32, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 2);
        ReserveLeaseSynchronously(lease, 1);
        ExpectCredit(controller, 1, 0, 32, 8, 24, 5, 5);
    }

    [Test]
    public void DisableGrantMustReturnUnusedCreditWithoutClosingTheLease()
    {
        var controller = NewGrantController(32, 128, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 4);
        ReserveLeaseSynchronously(lease, 4);
        ExpectCredit(controller, 1, 0, 128, 32, 108, 24, 12);

        lease.DisableGrant();
        ExpectCredit(controller, 1, 0, 128, 32, 120, 24, 0);
        var snapshot = controller.CaptureSendGrantSnapshot(1, 0);
        GrantEnsure(!snapshot.LeaseClosed && !snapshot.GrantEnabled,
            "DisableGrant must keep the generation-aware exact path available");
        lease.DisableGrant();
        ExpectCredit(controller, 1, 0, 128, 32, 120, 24, 0);
        GrantEnsure(lease.TryAcquire(4, CancellationToken.None),
            "the unsized pre-budget probe must remain usable after DisableGrant");
        ExpectCredit(controller, 1, 0, 128, 32, 116, 20, 0);
    }

    [Test]
    public async Task DisabledGrantMustKeepTheExactWaiterPathWithoutLateUnusedCredit()
    {
        var controller = NewGrantController(32, 128, 16);
        Task? pending = null;
        try
        {
            var lease = CaptureFirst(controller, 1, 0, 4);
            ReserveLeaseSynchronously(lease, 4);
            lease.DisableGrant();
            GrantEnsure(lease.TryAcquire(4, CancellationToken.None), "the exact probe should reserve four bytes");
            ReserveLeaseSynchronously(lease, 16);
            ExpectCredit(controller, 1, 0, 128, 32, 100, 4, 0);
            GrantEnsure(!lease.TryAcquire(8, CancellationToken.None),
                "a failed unsized probe must not join the flow waiter queue");
            GrantEnsure(controller.CaptureSendGrantSnapshot(1, 0).WaiterCount == 0,
                "the existing pre-credit budget admission must still precede asynchronous flow admission");

            pending = lease.AcquireAsync(8, CancellationToken.None).AsTask();
            GrantEnsure(!pending.IsCompleted, "eight bytes must wait for stream credit");
            controller.ApplyWindowUpdate(1, 0, 4);
            await pending.WaitAsync(GrantTestTimeout);
            ExpectCredit(controller, 1, 0, 128, 32, 96, 0, 0);
            var snapshot = controller.CaptureSendGrantSnapshot(1, 0);
            GrantEnsure(!snapshot.GrantEnabled && snapshot.WaiterCount == 0,
                "an exact waiter must not silently re-enable or replenish a disabled grant");
        }
        finally
        {
            controller.Complete(NewGrantCleanupException());
            await ObserveGrantTask(pending);
        }
    }

    [Test]
    public void DisposeMustCloseBeforeReturningGrantAndStillAllowTheOwnedItemRefund()
    {
        var controller = NewGrantController(32, 128, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 4);
        ReserveLeaseSynchronously(lease, 4);
        lease.Dispose();
        ExpectCredit(controller, 1, 0, 128, 32, 120, 24, 0);
        var snapshot = controller.CaptureSendGrantSnapshot(1, 0);
        GrantEnsure(snapshot.LeaseClosed && !snapshot.GrantEnabled,
            "returning Gi alone does not prevent a disposed handle from refilling later");
        GrantEnsure(ReferenceEquals(snapshot.CurrentLease, lease),
            "the closed context must remain the same generation's terminal record");
        ExpectClosedGrant(() => lease.TryAcquire(1, CancellationToken.None));
        GrantEnsure(!controller.TryAcquireSendCreditLease(1, 0, 1, CancellationToken.None, out var second)
            && second is null, "Dispose must not allow a second grant context for the same generation");

        lease.Dispose();
        lease.ReturnUnsent(4);
        ExpectCredit(controller, 1, 0, 128, 32, 124, 28, 0);
        lease.ReturnUnsent(4);
        ExpectCredit(controller, 1, 0, 128, 32, 128, 32, 0);
        ExpectInvalidGrantRefund(() => lease.ReturnUnsent(1));
        lease.Complete();
        GrantEnsure(controller.RetainedSendStreamCount == 0, "a fully credited completed lease must retire");
    }

    [Test]
    public void FailedFirstCaptureMustNotPublishStateOrJoinWaiters()
    {
        var controller = NewGrantController(16, 16, 8);
        using var cleanup = new GrantControllerCleanup(controller);
        GrantEnsure(controller.TryAcquireSendCredit(1, 0, 16), "setup must own the connection window");
        GrantEnsure(!controller.TryAcquireSendCreditLease(2, 0, 1, CancellationToken.None, out var lease)
            && lease is null, "no connection credit must produce a non-owning false result");
        var missing = controller.CaptureSendGrantSnapshot(2, 0);
        GrantEnsure(!missing.HasState && missing.ActiveStreams == 1 && missing.RetainedStreams == 1
            && missing.WaiterCount == 0, "a failed probe must not consume stream capacity or enqueue");
        CheckGrantConservation(controller, 16);
    }

    [Test]
    public void TombstonePressureMustNotCreateAFirstCaptureState()
    {
        var controller = NewGrantController(16, 32, 8, maxStreams: 1);
        using var cleanup = new GrantControllerCleanup(controller);
        var old = CaptureFirst(controller, 1, 0, 1);
        old.Complete();
        GrantEnsure(!controller.TryAcquireSendCreditLease(2, 0, 1, CancellationToken.None, out var lease)
            && lease is null, "a retained completed tombstone must defer a new probe without registration");
        var missing = controller.CaptureSendGrantSnapshot(2, 0);
        GrantEnsure(!missing.HasState && missing.ActiveStreams == 0 && missing.RetainedStreams == 1
            && missing.WaiterCount == 0, "tombstone pressure is not a live-stream capacity exception");
        controller.ApplyWindowUpdate(1, 0, 1);
        _ = CaptureFirst(controller, 2, 0, 1);
        ExpectCredit(controller, 2, 0, 32, 16, 31, 15, 0);
    }

    [Test]
    public void FirstCaptureMustPreserveHardCapacityAndCancellationErrors()
    {
        var controller = NewGrantController(16, 32, 8, maxStreams: 1);
        using var cleanup = new GrantControllerCleanup(controller);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            controller.TryAcquireSendCreditLease(1, 0, 1, canceled.Token, out _);
            throw new InvalidOperationException("expected the canceled first probe to throw");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { }
        GrantEnsure(controller.RetainedSendStreamCount == 0, "cancellation must precede first-state publication");
        _ = CaptureFirst(controller, 1, 0, 1);
        try
        {
            controller.TryAcquireSendCreditLease(2, 0, 1, CancellationToken.None, out _);
            throw new InvalidOperationException("a hard live-stream limit must not be swallowed as fallback");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted) { }
        ExpectCredit(controller, 1, 0, 32, 16, 31, 15, 0);
    }

    [Test]
    public void LegacyReservationOnALeasedStateMustRecallItsUnusedGrant()
    {
        var controller = NewGrantController(32, 128, 16);
        using var cleanup = new GrantControllerCleanup(controller);
        var lease = CaptureFirst(controller, 1, 0, 4);
        ReserveLeaseSynchronously(lease, 4);
        GrantEnsure(controller.TryAcquireSendCredit(1, 0, 4), "a second legacy handle must still reserve normally");
        ExpectCredit(controller, 1, 0, 128, 32, 116, 20, 0);
        var next = controller.AcquireSendCreditAsync(1, 0, 5, CancellationToken.None);
        GrantEnsure(next.IsCompletedSuccessfully, "legacy synchronous acquire must remain usable on the same state");
        next.GetAwaiter().GetResult();
        ExpectCredit(controller, 1, 0, 128, 32, 111, 15, 0);
    }

    private static readonly TimeSpan GrantTestTimeout = TimeSpan.FromSeconds(10);

    private static StreamFlowController NewGrantController(
        int streamWindow, int connectionWindow, int quantum, int maxStreams = 8)
        => new(streamWindow, connectionWindow, maxFramePayloadBytes: 65536,
            maxConcurrentStreams: maxStreams, sendGrantQuantum: quantum);

    private static StreamFlowController.SendCreditLease CaptureFirst(
        StreamFlowController controller, long requestId, ushort streamId, int bytes)
    {
        if (!controller.TryAcquireSendCreditLease(requestId, streamId, bytes, CancellationToken.None, out var lease)
            || lease is null)
            throw new InvalidOperationException("the test's first lease capture must succeed synchronously");
        return lease;
    }

    private static void ReserveLeaseSynchronously(StreamFlowController.SendCreditLease lease, int bytes)
    {
        var reservation = lease.AcquireAsync(bytes, CancellationToken.None);
        GrantEnsure(reservation.IsCompletedSuccessfully, "the test's uncontended lease reservation must complete inline");
        reservation.GetAwaiter().GetResult();
    }

    private static void ExpectCredit(
        StreamFlowController controller, long requestId, ushort streamId,
        int connectionWindow, int streamWindow, long free, long stream, long unused)
    {
        var snapshot = controller.CaptureSendGrantSnapshot(requestId, streamId);
        GrantEnsure(snapshot.HasState, $"expected state {requestId}/{streamId} to remain present");
        GrantEnsure(snapshot.ConnectionCredit == free && snapshot.StreamCredit == stream && snapshot.UnusedGrant == unused,
            $"credit {requestId}/{streamId}: expected F/C/G={free}/{stream}/{unused}; " +
            $"actual={snapshot.ConnectionCredit}/{snapshot.StreamCredit}/{snapshot.UnusedGrant}");
        GrantEnsure(snapshot.UnusedGrant >= 0 && snapshot.UnusedGrant <= Math.Max(0, snapshot.StreamCredit),
            "unused connection grant must not exceed this stream's remaining credit");
        GrantEnsure(snapshot.StreamCredit <= streamWindow, "stream credit must never exceed the negotiated window");
        CheckGrantConservation(controller, connectionWindow);
    }

    private static void CheckGrantConservation(StreamFlowController controller, int connectionWindow)
    {
        var snapshot = controller.CaptureSendGrantSnapshot(long.MinValue, 0);
        GrantEnsure(snapshot.TotalUnusedGrant >= 0 && snapshot.TotalDebt >= 0,
            "unused grants and actual item debt must remain non-negative");
        GrantEnsure(snapshot.ConnectionCredit + snapshot.TotalUnusedGrant + snapshot.TotalDebt == connectionWindow,
            $"F+sumG+sumD must equal Wc: {snapshot.ConnectionCredit}+{snapshot.TotalUnusedGrant}+" +
            $"{snapshot.TotalDebt}!={connectionWindow}");
    }

    private static void ExpectClosedGrant(Action action)
    {
        try { action(); }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ConnectionClosed) { return; }
        throw new InvalidOperationException("a closed lease accepted a new reservation");
    }

    private static void ExpectInvalidGrantRefund(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.Contains("more than once", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("an excess unsent refund must retain the existing double-return assertion");
    }

    private static async Task ObserveGrantTask(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(GrantTestTimeout); }
        catch (Exception error) when (error is not TimeoutException) { }
    }

    private static async Task ExpectGrantCancellation(Task task, CancellationToken token)
    {
        try { await task.WaitAsync(GrantTestTimeout); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { return; }
        throw new InvalidOperationException("the exact waiter did not preserve its cancellation token");
    }

    private static async Task ExpectGrantException(Task task, Exception expected)
    {
        try { await task.WaitAsync(GrantTestTimeout); }
        catch (Exception error) when (ReferenceEquals(error, expected)) { return; }
        throw new InvalidOperationException("the exact waiter lost its selected terminal exception instance");
    }

    private static void ExpectGrantException(Action action, Exception expected)
    {
        try { action(); }
        catch (Exception error) when (ReferenceEquals(error, expected)) { return; }
        throw new InvalidOperationException("the lease lost its selected terminal exception instance");
    }

    private static Exception NewGrantCleanupException() => new OperationCanceledException("grant test cleanup");

    private static void GrantEnsure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class GrantControllerCleanup : IDisposable
    {
        private readonly StreamFlowController _controller;

        internal GrantControllerCleanup(StreamFlowController controller) => _controller = controller;

        public void Dispose() => _controller.Complete(NewGrantCleanupException());
    }
}
