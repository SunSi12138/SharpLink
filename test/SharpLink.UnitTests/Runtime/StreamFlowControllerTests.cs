using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class StreamFlowControllerTests
{
    [Test]
    public async Task OneByteWindowShouldBlockUntilCreditReturns()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        await controller.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);

        var blocked = controller.AcquireSendCreditAsync(2, 0, 1, CancellationToken.None);
        Ensure(!blocked.IsCompleted, "second stream should wait for connection credit");

        controller.ApplyWindowUpdate(1, 0, 1);
        await blocked;
        Ensure(controller.SendConnectionCredit == 0, "resumed stream should own the returned byte");
    }

    [Test]
    public async Task OversizedItemShouldBorrowOnlyOnceAndRepayExactly()
    {
        var controller = new StreamFlowController(2, 8, 16);
        await controller.AcquireSendCreditAsync(10, 1, 6, CancellationToken.None);
        var blocked = controller.AcquireSendCreditAsync(10, 1, 1, CancellationToken.None);
        Ensure(!blocked.IsCompleted, "borrowed stream must wait until the oversized item is consumed");

        controller.ApplyWindowUpdate(10, 1, 6);
        await blocked;

        try
        {
            controller.ApplyWindowUpdate(10, 1, 2);
            throw new Exception("expected window overflow");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }
    }

    [Test]
    public async Task CancellationAndConnectionCloseShouldReleaseWaiters()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        await controller.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var canceled = controller.AcquireSendCreditAsync(2, 0, 1, cancellation.Token);
        cancellation.Cancel();
        await ExpectCancellation(canceled);

        var closed = controller.AcquireSendCreditAsync(3, 0, 1, CancellationToken.None);
        var exception = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed");
        controller.Complete(exception);
        await ExpectSameException(closed, exception);
    }

    [Test]
    public async Task ConnectionCreditShouldBeSharedInFifoOrder()
    {
        var controller = new StreamFlowController(4, 4, 1024);
        await controller.AcquireSendCreditAsync(1, 1, 4, CancellationToken.None);
        var second = controller.AcquireSendCreditAsync(2, 1, 2, CancellationToken.None);
        var third = controller.AcquireSendCreditAsync(3, 1, 1, CancellationToken.None);

        controller.ApplyWindowUpdate(1, 1, 2);
        await second;
        Ensure(!third.IsCompleted, "FIFO waiter should not bypass exhausted connection credit");

        controller.ApplyWindowUpdate(1, 1, 1);
        await third;
    }

    [Test]
    public async Task StreamCreditBlockedHeadShouldNotBlockAnEligibleStream()
    {
        var controller = new StreamFlowController(2, 4, 1024);
        await controller.AcquireSendCreditAsync(1, 0, 2, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var head = controller.AcquireSendCreditAsync(1, 0, 1, cancellation.Token);
        var next = controller.AcquireSendCreditAsync(2, 0, 1, CancellationToken.None);
        Ensure(next.IsCompletedSuccessfully,
            "a stream-credit-blocked head must not stall an independent eligible stream");

        cancellation.Cancel();
        await ExpectCancellation(head);
        await next;
    }

    [Test]
    public async Task ReceiveWindowShouldRejectOverrunAndBatchHalfWindowUpdates()
    {
        var controller = new StreamFlowController(4, 8, 1024);
        controller.AcceptReceived(1, 0, 4);
        try
        {
            controller.AcceptReceived(1, 0, 1);
            throw new Exception("expected receive window violation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        Ensure(controller.RecordConsumed(1, 0, 1) == 0, "less than half a stream window should batch");
        Ensure(controller.RecordConsumed(1, 0, 1) == 2, "half a stream window should emit an update");
        Ensure(controller.FlushConsumed(1, 0) == 0, "emitted credit should not be duplicated");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConnectionThresholdShouldNotStrandConsumedCreditOnAnotherOpenStream()
    {
        var receiver = new StreamFlowController(4, 4, 16);
        receiver.AcceptReceived(1, 1, 1);
        Ensure(receiver.RecordConsumed(1, 1, 1) == 0,
            "the first stream should batch below both thresholds");

        receiver.AcceptReceived(2, 1, 1);
        Ensure(receiver.RecordConsumed(2, 1, 1) == 1,
            "the second stream should reach the connection threshold");

        var pendingField = typeof(StreamFlowController).GetField(
            "_pendingConnectionConsumed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("pending connection credit field was not found");
        Ensure((long)pendingField.GetValue(receiver)! == 0,
            "reaching the connection threshold must flush consumed credit from every contributing stream");
        Ensure(receiver.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit),
            "the threshold must expose the other stream's pending credit");
        Ensure(requestId == 1 && streamId == 1 && credit == 1,
            "the additional update must retain its exact stream identity and byte count");
        Ensure(!receiver.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "each contributing stream credit must be emitted exactly once");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FailedSendStreamShouldAcceptInFlightCreditBeforeReusingCapacity()
    {
        var controller = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 1);
        await controller.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        controller.CompleteSendStream(
            1,
            0,
            new SharpLinkException(SharpLinkErrorCode.Cancelled, "consumer abandoned"));

        Ensure(controller.SendConnectionCredit == 0,
            "failed stream must not invent credit while a receiver update can still be in flight");
        controller.ApplyWindowUpdate(1, 0, 4);
        Ensure(controller.SendConnectionCredit == 4,
            "the receiver's final update must reclaim the exact outstanding credit");
        await controller.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None);
        Ensure(controller.SendConnectionCredit == 0, "new stream should reuse reclaimed capacity");
    }

    [Test]
    public async Task UnsentFrameShouldReturnCreditAndAdmitTheNextWaiter()
    {
        var controller = new StreamFlowController(4, 4, 1024);
        await controller.AcquireSendCreditAsync(1, 1, 4, CancellationToken.None);
        var blocked = controller.AcquireSendCreditAsync(2, 1, 4, CancellationToken.None);
        Ensure(!blocked.IsCompleted, "next stream should wait while the unsent frame owns credit");

        controller.ReturnUnsentCredit(1, 1, 4);

        await blocked;
        Ensure(controller.SendConnectionCredit == 0,
            "the next waiter should atomically acquire the returned connection credit");
    }

    [Test]
    public async Task UnknownWindowUpdateShouldRemainAProtocolViolation()
    {
        var controller = new StreamFlowController(4, 4, 1024);
        try
        {
            controller.ApplyWindowUpdate(99, 1, 1);
            throw new Exception("expected unknown stream violation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task RequestCancelShouldAbortAllResponseStreamsEvenAfterCallDispatchCompleted()
    {
        var controller = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 2);
        await controller.AcquireSendCreditAsync(7, 0, 4, CancellationToken.None);
        await controller.AcquireSendCreditAsync(7, 1, 4, CancellationToken.None);

        controller.AbortSendStreams(
            7,
            new SharpLinkException(SharpLinkErrorCode.Cancelled, "remote cancel"));

        Ensure(controller.SendConnectionCredit == 8, "request cancel must reclaim every response stream credit");
        controller.CompleteSendStream(7, 0);
        controller.CompleteSendStream(7, 1);
        await controller.AcquireSendCreditAsync(8, 0, 4, CancellationToken.None);
        await controller.AcquireSendCreditAsync(9, 0, 4, CancellationToken.None);
    }

    private static async Task ExpectCancellation(ValueTask pending)
    {
        try
        {
            await pending;
            throw new Exception("expected cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ExpectSameException(ValueTask pending, Exception expected)
    {
        try
        {
            await pending;
            throw new Exception("expected terminal exception");
        }
        catch (Exception actual) when (ReferenceEquals(actual, expected))
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
