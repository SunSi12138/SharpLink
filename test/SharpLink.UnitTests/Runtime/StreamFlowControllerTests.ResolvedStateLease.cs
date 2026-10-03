using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowControllerTests
{
    [Test]
    public async Task ResolvedSendLeaseShouldNotReachReusedSameKeyState()
    {
        var controller = new StreamFlowController(1, 1, 1024);

        await controller.AcquireSendCreditAsync(701, 3, 1, CancellationToken.None);
        var stale = controller.ResolveSendCreditLease(701, 3);
        Ensure(stale.IsResolved, "the first send lifecycle should resolve a lease");

        controller.ApplyWindowUpdate(701, 3, 1);
        controller.CompleteSendStream(701, 3);

        await controller.AcquireSendCreditAsync(701, 3, 1, CancellationToken.None);
        var current = controller.ResolveSendCreditLease(701, 3);
        Ensure(current.IsResolved, "the reused key should resolve a new lifecycle");

        try
        {
            _ = controller.TryAcquireSendCredit(in stale, 1);
            throw new Exception("expected the stale send lease to be rejected");
        }
        catch (SharpLinkException)
        {
        }

        controller.ApplyWindowUpdate(701, 3, 1);
        Ensure(controller.TryAcquireSendCredit(in current, 1),
            "the current generation must remain usable after rejecting the stale lease");
    }

    [Test]
    public async Task ResolvedReceiveLeaseShouldNotReachReusedSameKeyState()
    {
        var controller = new StreamFlowController(1, 1, 1024);

        var stale = controller.ResolveReceiveCreditLease(702, 4);
        controller.AcceptReceived(in stale, 1);
        Ensure(controller.RecordConsumed(in stale, 1) == 1,
            "the original receive lifecycle should return its byte exactly once");
        Ensure(controller.FlushConsumed(in stale) == 0,
            "the completed receive lifecycle should have no duplicate pending credit");

        var current = controller.ResolveReceiveCreditLease(702, 4);
        Ensure(current.IsResolved, "the reused receive key should resolve a new generation");

        try
        {
            controller.AcceptReceived(in stale, 1);
            throw new Exception("expected the stale receive lease to be rejected");
        }
        catch (SharpLinkException)
        {
        }

        Ensure(controller.RecordConsumed(in stale, 1) == 0,
            "late consumption through a stale lease must not credit the new lifecycle");
        controller.AcceptReceived(in current, 1);
        Ensure(controller.RecordConsumed(in current, 1) == 1,
            "the current receive lifecycle must retain its independent credit");
    }

    [Test]
    public async Task ResolvedSendWaiterCancellationShouldPreserveCurrentLease()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        await controller.AcquireSendCreditAsync(703, 5, 1, CancellationToken.None);
        var lease = controller.ResolveSendCreditLease(703, 5);

        using var cancellation = new CancellationTokenSource();
        var blocked = controller.AcquireSendCreditAsync(in lease, 1, cancellation.Token);
        Ensure(!blocked.IsCompleted, "the resolved waiter should block on exhausted stream credit");

        cancellation.Cancel();
        await ExpectCancellation(blocked);

        controller.ApplyWindowUpdate(703, 5, 1);
        Ensure(controller.TryAcquireSendCredit(in lease, 1),
            "canceling a resolved waiter must not invalidate the live stream lease");
    }

    [Test]
    public async Task ResolvedUnsentCreditShouldBeReturnedExactlyOnce()
    {
        var controller = new StreamFlowController(4, 8, 1024);
        await controller.AcquireSendCreditAsync(704, 6, 2, CancellationToken.None);
        var lease = controller.ResolveSendCreditLease(704, 6);

        controller.ReturnUnsentCredit(in lease, 2);
        try
        {
            controller.ReturnUnsentCredit(in lease, 2);
            throw new Exception("expected duplicate unsent-credit return to be rejected");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Test]
    public async Task MultipleResolvedReceiveHandlesShouldNotDoubleReturnCredit()
    {
        var controller = new StreamFlowController(4, 8, 1024);
        var first = controller.ResolveReceiveCreditLease(705, 7);
        var second = controller.ResolveReceiveCreditLease(705, 7);

        controller.AcceptReceived(in first, 2);
        Ensure(controller.RecordConsumed(in first, 2) == 2,
            "the first handle should return the outstanding bytes");

        try
        {
            _ = controller.RecordConsumed(in second, 1);
            throw new Exception("expected the second handle to be unable to double-return credit");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolvedReceiveThresholdsShouldPreserveCrossStreamFlush()
    {
        var controller = new StreamFlowController(4, 4, 1024);
        var first = controller.ResolveReceiveCreditLease(706, 1);
        var second = controller.ResolveReceiveCreditLease(707, 2);

        controller.AcceptReceived(in first, 1);
        Ensure(controller.RecordConsumed(in first, 1) == 0,
            "the first resolved stream should batch below both thresholds");

        controller.AcceptReceived(in second, 1);
        Ensure(controller.RecordConsumed(in second, 1) == 1,
            "the second resolved stream should trip the connection threshold");
        Ensure(controller.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit),
            "the cross-stream flush should expose the first stream's pending credit");
        Ensure(requestId == 706 && streamId == 1 && credit == 1,
            "resolved-state flushing must preserve the original stream identity");
        Ensure(!controller.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "cross-stream resolved credit must be emitted exactly once");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TerminalShouldInvalidateResolvedHandlesWithoutCreditingAReplacement()
    {
        var controller = new StreamFlowController(2, 2, 1024);
        await controller.AcquireSendCreditAsync(708, 8, 1, CancellationToken.None);
        var send = controller.ResolveSendCreditLease(708, 8);
        var receive = controller.ResolveReceiveCreditLease(709, 9);
        controller.AcceptReceived(in receive, 1);

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed");
        controller.Complete(terminal);

        try
        {
            _ = controller.TryAcquireSendCredit(in send, 1);
            throw new Exception("expected the terminal to reject the retained send lease");
        }
        catch (SharpLinkException exception)
        {
            Ensure(ReferenceEquals(exception, terminal),
                "resolved send access after terminal should surface the session terminal");
        }

        Ensure(controller.RecordConsumed(in receive, 1) == 0,
            "late receive consumption after terminal must not mutate flow credit");
    }

    [Test]
    public async Task ResolvedReceiveLeaseShouldRespectConcurrentStreamLimit()
    {
        var controller = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 1);
        _ = controller.ResolveReceiveCreditLease(710, 1);

        try
        {
            _ = controller.ResolveReceiveCreditLease(711, 1);
            throw new Exception("expected receive stream capacity exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolvedReceiveStatePoolShouldSurviveOneHundredThousandReuses()
    {
        const int cycles = 100_000;
        var controller = new StreamFlowController(
            streamWindow: 1,
            connectionWindow: 1,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: 1);

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var requestId = cycle + 1L;
            var lease = controller.ResolveReceiveCreditLease(requestId, 1);
            controller.AcceptReceived(in lease, 1);
            Ensure(controller.RecordConsumed(in lease, 1) == 1,
                "each pooled receive generation must return its byte exactly once");
            Ensure(controller.FlushConsumed(in lease) == 0,
                "each pooled receive generation must retire without duplicate credit");
        }

        var finalLease = controller.ResolveReceiveCreditLease(cycles + 1L, 1);
        controller.AcceptReceived(in finalLease, 1);
        Ensure(controller.RecordConsumed(in finalLease, 1) == 1,
            "the receive pool must remain usable after 100k generation changes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MultipleResolvedSendHandlesShouldNotDoubleReturnCredit()
    {
        var controller = new StreamFlowController(2, 4, 1024);
        await controller.AcquireSendCreditAsync(712, 2, 1, CancellationToken.None);
        var first = controller.ResolveSendCreditLease(712, 2);
        var second = controller.ResolveSendCreditLease(712, 2);

        controller.ReturnUnsentCredit(in first, 1);
        try
        {
            controller.ReturnUnsentCredit(in second, 1);
            throw new Exception("expected a second handle to be unable to double-return send credit");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Test]
    public async Task AbortSendStreamsShouldPoisonResolvedLeaseWithoutDoubleReturn()
    {
        var controller = new StreamFlowController(2, 4, 1024);
        await controller.AcquireSendCreditAsync(713, 3, 1, CancellationToken.None);
        var lease = controller.ResolveSendCreditLease(713, 3);
        var abort = new SharpLinkException(SharpLinkErrorCode.Cancelled, "aborted");

        controller.AbortSendStreams(713, abort);
        try
        {
            _ = controller.TryAcquireSendCredit(in lease, 1);
            throw new Exception("expected the aborted resolved lease to remain poisoned");
        }
        catch (SharpLinkException exception)
        {
            Ensure(ReferenceEquals(exception, abort),
                "resolved send access should preserve the abort exception");
        }

        controller.ReturnUnsentCredit(in lease, 1);
        Ensure(controller.SendConnectionCredit == 4,
            "late unsent-credit return after abort must not duplicate restored connection credit");
        controller.CompleteSendStream(713, 3, abort);
    }

    [Test]
    public async Task CompletedReceiveTombstoneShouldKeepResolvedLeaseUntilFinalCredit()
    {
        var controller = new StreamFlowController(2, 4, 1024, maxConcurrentStreams: 1);
        var lease = controller.ResolveReceiveCreditLease(714, 4);
        controller.AcceptReceived(in lease, 2);
        Ensure(controller.FlushConsumed(in lease) == 0,
            "completion with outstanding bytes must retain the receive tombstone");

        try
        {
            _ = controller.ResolveReceiveCreditLease(714, 4);
            throw new Exception("expected the completed receive tombstone to reject a new lease");
        }
        catch (SharpLinkException)
        {
        }

        Ensure(controller.RecordConsumed(in lease, 2) == 2,
            "the original lease must return the tombstone's final outstanding credit");
        var replacement = controller.ResolveReceiveCreditLease(715, 4);
        Ensure(replacement.IsResolved,
            "final credit should retire the tombstone and release receive capacity");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolvedReceiveLeaseShouldPreserveStreamThresholdBatching()
    {
        var controller = new StreamFlowController(4, 8, 1024);
        var lease = controller.ResolveReceiveCreditLease(716, 5);
        controller.AcceptReceived(in lease, 4);

        Ensure(controller.RecordConsumed(in lease, 1) == 0,
            "resolved receive credit below half-window must remain batched");
        Ensure(controller.RecordConsumed(in lease, 1) == 2,
            "resolved receive credit at half-window must emit the same stream update");
        Ensure(controller.FlushConsumed(in lease) == 0,
            "flushing the completed resolved stream must not duplicate emitted credit");
        Ensure(controller.RecordConsumed(in lease, 2) == 2,
            "late buffered consumption must return only the remaining outstanding credit");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolvedReceiveLeaseShouldPreserveStreamAndConnectionExhaustion()
    {
        var streamController = new StreamFlowController(2, 8, 1024);
        var streamLease = streamController.ResolveReceiveCreditLease(717, 6);
        streamController.AcceptReceived(in streamLease, 2);
        try
        {
            streamController.AcceptReceived(in streamLease, 1);
            throw new Exception("expected resolved stream-window exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        var connectionController = new StreamFlowController(4, 4, 1024);
        var first = connectionController.ResolveReceiveCreditLease(718, 1);
        var second = connectionController.ResolveReceiveCreditLease(719, 2);
        connectionController.AcceptReceived(in first, 4);
        try
        {
            connectionController.AcceptReceived(in second, 1);
            throw new Exception("expected resolved connection-window exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        await Task.CompletedTask;
    }
}
