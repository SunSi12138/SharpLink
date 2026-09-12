using System.Threading;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowControllerTests
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

        // A benign double return can overshoot the window (the peer may
        // release credit at detach and return it again as it drains the
        // frames that arrive afterwards): the excess is clamped.
        controller.ApplyWindowUpdate(10, 1, 2);
        Ensure(controller.SendConnectionCredit <= 16,
            "the clamped excess must not push the connection credit past its window");
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
    public async Task MultiKeyReceiveExhaustionShouldFlushAllContributingConnectionCredit()
    {
        var receiver = new StreamFlowController(8, 8, 1024);
        receiver.AcceptReceived(41, 1, 3);
        receiver.AcceptReceived(42, 2, 3);
        receiver.AcceptReceived(43, 3, 2);

        try
        {
            receiver.AcceptReceived(44, 4, 1);
            throw new Exception("expected exhausted connection credit violation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        Ensure(receiver.RecordConsumed(41, 1, 3) == 0,
            "the first partial stream consume should remain pending below both update thresholds");
        Ensure(receiver.RecordConsumed(42, 2, 3) == 3,
            "the connection threshold should return the current key's exact pending credit");
        Ensure(receiver.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit),
            "the connection threshold should flush the other contributing key");
        Ensure(requestId == 41 && streamId == 1 && credit == 3,
            "the queued cross-key update must preserve its original key and exact credit");
        Ensure(!receiver.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "each contributing key must be flushed exactly once");

        receiver.AcceptReceived(44, 4, 6);
        try
        {
            receiver.AcceptReceived(44, 4, 1);
            throw new Exception("expected the exact flushed credit to be fully consumed");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task RecordConsumedDuplicateAndOverCreditShouldBeProtocolViolations()
    {
        var duplicate = new StreamFlowController(4, 8, 1024);
        duplicate.AcceptReceived(51, 1, 2);
        duplicate.AcceptReceived(52, 2, 4);
        Ensure(duplicate.RecordConsumed(51, 1, 2) == 2,
            "the original consumed credit should be returned once");
        try
        {
            duplicate.RecordConsumed(51, 1, 2);
            throw new Exception("expected duplicate consumed credit violation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        var overCredit = new StreamFlowController(4, 8, 1024);
        overCredit.AcceptReceived(61, 1, 2);
        overCredit.AcceptReceived(62, 2, 4);
        try
        {
            overCredit.RecordConsumed(61, 1, 3);
            throw new Exception("expected over-credit consumed violation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ConnectionThresholdFlushShouldClearPendingCreditFromEveryReceiveState()
    {
        var receiver = new StreamFlowController(4, 4, 16);
        receiver.AcceptReceived(1, 1, 1);
        Ensure(receiver.RecordConsumed(1, 1, 1) == 0,
            "the first stream should leave credit pending before the connection threshold");
        receiver.AcceptReceived(2, 1, 1);
        Ensure(receiver.RecordConsumed(2, 1, 1) == 1,
            "the second stream should flush both pending receive states");

        Ensure(receiver.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit),
            "the first connection flush should enqueue the other stream's credit");
        Ensure(requestId == 1 && streamId == 1 && credit == 1,
            "the first connection flush must preserve the other stream's exact credit");
        Ensure(!receiver.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "the first connection flush should leave no duplicate updates");

        receiver.AcceptReceived(3, 1, 1);
        Ensure(receiver.RecordConsumed(3, 1, 1) == 0,
            "a new partial receive state should remain pending before the next threshold");
        receiver.AcceptReceived(4, 1, 1);
        Ensure(receiver.RecordConsumed(4, 1, 1) == 1,
            "the next connection threshold should return the current stream's credit");

        Ensure(receiver.TryTakeConsumedCreditUpdate(out requestId, out streamId, out credit),
            "the second connection flush should enqueue its only other pending stream");
        Ensure(requestId == 3 && streamId == 1 && credit == 1,
            "already-flushed receive states must not emit duplicate credit");
        Ensure(!receiver.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "the second connection flush should not recreate old pending credit");
        await Task.CompletedTask;
    }

    [Test]
    public async Task QueuedCrossStreamCreditMustSurviveReceiveStateReuseBeforeDrain()
    {
        var receiver = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 2);
        receiver.AcceptReceived(71, 1, 1);
        var retired = GetReceiveState(receiver, 71, 1);
        Ensure(receiver.RecordConsumed(71, 1, 1) == 0,
            "the first stream credit must remain pending until the connection threshold is reached");

        receiver.AcceptReceived(72, 1, 1);
        Ensure(receiver.RecordConsumed(72, 1, 1) == 1,
            "the second stream must trigger a connection-threshold flush for the current key");

        Ensure(receiver.FlushConsumed(71, 1) == 0,
            "the already-flushed stream must complete without emitting duplicate credit");
        Ensure(GetReceiveStateCount(receiver) == 1,
            "the completed stream must leave its dictionary slot before reuse");

        receiver.AcceptReceived(73, 1, 2);
        var reused = GetReceiveState(receiver, 73, 1);
        Ensure(ReferenceEquals(retired, reused),
            "the newly admitted stream must reuse the completed stream's local state object");

        Ensure(receiver.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit),
            "the earlier connection flush must retain its queued cross-stream credit after reuse");
        Ensure(requestId == 71 && streamId == 1 && credit == 1,
            "the queued update must preserve the retired stream identity and exact credit");
        Ensure(receiver.RecordConsumed(73, 1, 2) == 2,
            "draining an old queued update must not alter the reused stream's pending credit");
        Ensure(!receiver.TryTakeConsumedCreditUpdate(out _, out _, out _),
            "the old queued update must be drained exactly once");
        await Task.CompletedTask;
    }

    [Test]
    public async Task LateWindowUpdateForRemovedStreamShouldBeDiscarded()
    {
        var sender = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 1);
        await sender.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        sender.CompleteSendStream(1, 0);
        sender.ApplyWindowUpdate(1, 0, 4);
        Ensure(sender.SendConnectionCredit == 4,
            "the final credit return must reclaim the exact outstanding capacity");

        // A second, obsolete credit return can race the stream removal (the
        // peer may return credit for frames it drained after completion); it
        // must be discarded without a protocol violation or double counting.
        sender.ApplyWindowUpdate(1, 0, 1);
        Ensure(sender.SendConnectionCredit == 4,
            "the obsolete late credit must not corrupt the connection budget");
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
    public async Task CompletedSendTombstoneShouldBackpressureReplacementUntilPeerReleasesCapacity()
    {
        var sender = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 1);
        var receiver = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 1);
        await sender.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);
        receiver.AcceptReceived(1, 0, 1);

        try
        {
            await sender.AcquireSendCreditAsync(2, 0, 1, CancellationToken.None);
            throw new Exception("expected active stream capacity exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
        }

        sender.CompleteSendStream(
            1,
            0,
            new SharpLinkException(SharpLinkErrorCode.Cancelled, "consumer abandoned"));
        sender.CompleteSendStream(1, 0);
        Ensure(sender.ActiveSendStreamCount == 0,
            "completion must idempotently release the active count");
        Ensure(sender.RetainedSendStreamCount == 1,
            "the sender must retain the completed state until its final credit returns");

        var replacement = sender.AcquireSendCreditAsync(2, 0, 1, CancellationToken.None);
        Ensure(!replacement.IsCompleted,
            "replacement must wait while the peer still counts the completed receive stream");

        Ensure(receiver.RecordConsumed(1, 0, 1) == 0,
            "sub-threshold credit should remain batched until receive completion");
        var finalCredit = receiver.FlushConsumed(1, 0);
        Ensure(finalCredit == 1, "receive completion must flush the final stream credit");
        sender.ApplyWindowUpdate(1, 0, finalCredit);

        await replacement;
        Ensure(sender.ActiveSendStreamCount == 1,
            "the replacement should become active only after the old state is released");
        Ensure(sender.RetainedSendStreamCount == 1,
            "the replacement should atomically reuse the released retained-state slot");
        receiver.AcceptReceived(2, 0, 1);
    }

    [Test]
    public async Task RetainedSendTombstonesShouldStayBoundedAndBackpressureNewStreams()
    {
        var controller = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 2);
        await controller.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);
        controller.CompleteSendStream(1, 0);
        await controller.AcquireSendCreditAsync(2, 0, 1, CancellationToken.None);
        controller.CompleteSendStream(2, 0);

        Ensure(controller.ActiveSendStreamCount == 0,
            "both completed streams should release their active counts");
        Ensure(controller.RetainedSendStreamCount == 2,
            "retained tombstones must stop at the negotiated stream limit");

        var replacement = controller.AcquireSendCreditAsync(3, 0, 1, CancellationToken.None);
        Ensure(!replacement.IsCompleted,
            "a new stream must backpressure instead of growing retained state beyond the limit");
        Ensure(controller.RetainedSendStreamCount == 2,
            "waiting for capacity must not allocate another send state");

        controller.ApplyWindowUpdate(1, 0, 1);
        await replacement;
        Ensure(controller.ActiveSendStreamCount == 1,
            "returned terminal credit should admit one replacement");
        Ensure(controller.RetainedSendStreamCount == 2,
            "admission must reuse the released slot without exceeding the bound");
    }

    [Test]
    public async Task TombstoneCapacityShouldRetainAtMostOneReplacementWaiter()
    {
        var controller = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 1);
        await controller.AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);
        controller.CompleteSendStream(1, 0);

        using var cancellation = new CancellationTokenSource();
        var firstReplacement = controller.AcquireSendCreditAsync(2, 0, 1, cancellation.Token);
        Ensure(!firstReplacement.IsCompleted,
            "one replacement should backpressure until retained capacity is released");

        try
        {
            await controller.AcquireSendCreditAsync(3, 0, 1, CancellationToken.None);
            throw new Exception("expected pending replacement capacity exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
        }

        cancellation.Cancel();
        await ExpectCancellation(firstReplacement);

        var nextReplacement = controller.AcquireSendCreditAsync(3, 0, 1, CancellationToken.None);
        Ensure(!nextReplacement.IsCompleted,
            "canceling the bounded waiter should make its slot reusable");
        controller.ApplyWindowUpdate(1, 0, 1);
        await nextReplacement;
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
    public async Task UnknownWindowUpdateShouldBeDiscarded()
    {
        // A credit return for a stream that no longer exists (completed and
        // fully credited, or never created) is a benign wire race: the peer
        // may return credit for frames it drained after the stream finished.
        // The obsolete credit must be discarded without a protocol violation
        // or double counting.
        var controller = new StreamFlowController(4, 4, 1024);
        controller.ApplyWindowUpdate(99, 1, 1);
        Ensure(controller.SendConnectionCredit == 4,
            "an unknown-stream credit return must not corrupt the connection budget");
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

    [Test]
    public async Task RejectedStreamCompletionFrameShouldReleaseItsFlowControlSlot()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options =>
            {
                options.FlowControl.MaxSendQueueBytes = 1;
                options.Protocol.MaxConcurrentStreamsPerConnection = 1;
            })
            .Build();
        var input = new Pipe();
        var output = new BlockingFlushPipeWriter();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stream-completion-capacity",
            input.Reader,
            output,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 4,
            connectionReceiveWindowBytes: 4);
        await session.AcquireStreamSendCreditAsync(1, 1, 1, CancellationToken.None);
        session.ApplyWindowUpdate(1, new ProtocolV2WindowUpdate(1, 1));
        session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            try
            {
                session.SendStreamCompleteAsync(1, 1);
                throw new Exception("expected the bounded send queue to reject StreamComplete");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
            }

            await session.AcquireStreamSendCreditAsync(2, 1, 1, CancellationToken.None);
        }
        finally
        {
            output.ReleaseFlush();
        }
    }

    [Test]
    public async Task CompletedSendStateShouldBeReusedAndResetForReplacement()
    {
        var controller = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 1);
        await controller.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        var retired = GetSendState(controller, 1, 0);
        var retiredLease = GetPrivateField<long>(retired, "Lease");

        controller.CompleteSendStream(1, 0);
        controller.ApplyWindowUpdate(1, 0, 4);
        Ensure(GetPrivateField<long>(retired, "Credit") == 4,
            "a pooled send state must hold the full window before being rented again");
        await controller.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None);

        var reused = GetSendState(controller, 2, 0);
        Ensure(ReferenceEquals(retired, reused),
            "the replacement stream must reuse the released send-state object");
        Ensure(GetPrivateField<long>(reused, "Lease") > retiredLease,
            "reuse must advance the send-state lease");
        Ensure(!GetPrivateField<bool>(reused, "Completed"),
            "a reused send state must not retain the previous completion marker");
        Ensure(GetPrivateField<object?>(reused, "AbortException") is null,
            "a reused send state must not retain the previous abort exception");
        Ensure(GetPrivateField<object?>(reused, "Next") is null,
            "an active reused send state must not retain a pool link");
    }

    [Test]
    public async Task SendStatePoolShouldClearOnConnectionCompletion()
    {
        var controller = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 1);
        await controller.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        controller.CompleteSendStream(1, 0);
        controller.ApplyWindowUpdate(1, 0, 4);
        Ensure(GetPooledSendStateLinkCount(controller) == 1,
            "a fully released send state should be pooled");

        controller.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed"));
        Ensure(GetPrivateField<object?>(controller, "_pooledSendStates") is null,
            "connection completion must clear the send-state pool");
        Ensure(GetPrivateField<int>(controller, "_pooledSendStateOverflowCount") == 0,
            "connection completion must clear the send-state pool counter");
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

    private static object GetReceiveState(StreamFlowController controller, long requestId, ushort streamId)
    {
        var streamKeyType = typeof(StreamFlowController).GetNestedType(
            "StreamKey",
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("receive stream key type was not found");
        var key = Activator.CreateInstance(streamKeyType, new object[] { requestId, streamId })
            ?? throw new Exception("receive stream key could not be created");
        var states = GetPrivateField<object>(controller, "_receiveStates");
        var tryGetValue = states.GetType().GetMethod("TryGetValue")
            ?? throw new Exception("receive state lookup was not found");
        var arguments = new object?[] { key, null };
        if (tryGetValue.Invoke(states, arguments) is not true || arguments[1] is null)
            throw new Exception($"receive state ({requestId}, {streamId}) was not found");
        return arguments[1]!;
    }

    private static object GetSendState(StreamFlowController controller, long requestId, ushort streamId)
    {
        var streamKeyType = typeof(StreamFlowController).GetNestedType(
            "StreamKey",
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("send stream key type was not found");
        var key = Activator.CreateInstance(streamKeyType, new object[] { requestId, streamId })
            ?? throw new Exception("send stream key could not be created");
        var states = GetPrivateField<object>(controller, "_sendStates");
        var tryGetValue = states.GetType().GetMethod("TryGetValue")
            ?? throw new Exception("send state lookup was not found");
        var arguments = new object?[] { key, null };
        if (tryGetValue.Invoke(states, arguments) is not true || arguments[1] is null)
            throw new Exception($"send state ({requestId}, {streamId}) was not found");
        return arguments[1]!;
    }

    private static int GetPooledSendStateLinkCount(StreamFlowController controller)
    {
        var state = GetPrivateField<object?>(controller, "_pooledSendStates");
        var count = 0;
        while (state is not null)
        {
            if (++count > 128)
                throw new Exception("send-state pool link chain exceeded its bounded capacity");
            state = GetPrivateField<object?>(state, "Next");
        }
        return count;
    }

    private static int GetReceiveStateCount(StreamFlowController controller)
    {
        var states = GetPrivateField<object>(controller, "_receiveStates");
        var count = states.GetType().GetProperty("Count")?.GetValue(states)
            ?? throw new Exception("receive state count was not found");
        return (int)count;
    }

    private static int GetPooledReceiveStateLinkCount(StreamFlowController controller)
    {
        var state = GetPrivateField<object?>(controller, "_pooledReceiveStates");
        var count = 0;
        while (state is not null)
        {
            if (++count > 128)
                throw new Exception("receive-state pool link chain exceeded its bounded capacity");
            state = GetPrivateField<object?>(state, "Next");
        }
        return count;
    }

    private static T GetPrivateField<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception($"private field {name} was not found");
        return (T)field.GetValue(owner)!;
    }

    private static void EnsureReceiveStateCleared(object state, string description)
    {
        Ensure(GetPrivateField<long>(state, "Credit") == 0, $"{description} credit must be cleared");
        Ensure(GetPrivateField<long>(state, "PendingConsumed") == 0,
            $"{description} pending credit must be cleared");
        Ensure(!GetPrivateField<bool>(state, "Completed"),
            $"{description} completion marker must be cleared");
        Ensure(GetPrivateField<object?>(state, "Next") is null,
            $"{description} pool link must be cleared");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingFlushPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource<FlushResult> _flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override void CancelPendingFlush() => _flush.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) => ReleaseFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushStarted.TrySetResult();
            return new ValueTask<FlushResult>(_flush.Task.WaitAsync(cancellationToken));
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

        internal void ReleaseFlush()
            => _flush.TrySetResult(new FlushResult(isCanceled: false, isCompleted: false));
    }
}
