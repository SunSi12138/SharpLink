using System.Threading;
using System.IO.Pipelines;

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
    public async Task CompletedReceiveStateShouldReleaseCapacityAfterItsFinalCreditReturns()
    {
        const int maxConcurrentStreams = 128;
        var receiver = new StreamFlowController(
            streamWindow: 4,
            connectionWindow: 512,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: maxConcurrentStreams);
        for (var requestId = 1; requestId <= maxConcurrentStreams; requestId++)
            receiver.AcceptReceived(requestId, 1, 1);

        try
        {
            receiver.AcceptReceived(maxConcurrentStreams + 1, 1, 1);
            throw new Exception("expected receive stream capacity exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        Ensure(receiver.FlushConsumed(1, 1) == 0,
            "completion should retain a receive state until its final credit returns");
        Ensure(receiver.RecordConsumed(1, 1, 1) == 1,
            "the final credit should be emitted when the completed receive state is released");
        receiver.AcceptReceived(maxConcurrentStreams + 1, 1, 1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExistingReceiveStateAtCapacityShouldKeepItsCreditAndReleaseTheSlot()
    {
        var receiver = new StreamFlowController(
            streamWindow: 4,
            connectionWindow: 16,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: 1);

        receiver.AcceptReceived(1, 1, 1);
        receiver.AcceptReceived(1, 1, 1);
        try
        {
            receiver.AcceptReceived(2, 1, 1);
            throw new Exception("expected the second receive state to exceed the bounded capacity");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        Ensure(receiver.RecordConsumed(1, 1, 1) == 0,
            "the first partial consume should remain below the stream update threshold");
        Ensure(receiver.RecordConsumed(1, 1, 1) == 2,
            "the existing state at capacity must retain both reserved bytes and flush them once");
        Ensure(receiver.FlushConsumed(1, 1) == 0,
            "flushing an already-returned receive state must not duplicate credit");

        receiver.AcceptReceived(2, 1, 1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task RemovedReceiveStateShouldReuseTheClassReferenceWithResetFields()
    {
        var receiver = new StreamFlowController(4, 8, 1024, maxConcurrentStreams: 2);
        receiver.AcceptReceived(1, 1, 1);
        var first = GetReceiveState(receiver, 1, 1);
        receiver.AcceptReceived(1, 1, 1);
        Ensure(ReferenceEquals(first, GetReceiveState(receiver, 1, 1)),
            "an existing receive key must retain its dictionary class reference");

        receiver.AcceptReceived(2, 1, 2);
        var second = GetReceiveState(receiver, 2, 1);
        Ensure(!ReferenceEquals(first, second), "different active receive keys require distinct states");

        Ensure(receiver.FlushConsumed(1, 1) == 0, "completion should retain the first partial state");
        Ensure(receiver.RecordConsumed(1, 1, 2) == 2, "the first final credit should release its state");
        Ensure(receiver.FlushConsumed(2, 1) == 0, "completion should retain the second partial state");
        Ensure(receiver.RecordConsumed(2, 1, 2) == 2, "the second final credit should release its state");

        receiver.AcceptReceived(3, 1, 1);
        var reused = GetReceiveState(receiver, 3, 1);
        Ensure(ReferenceEquals(second, reused), "the last removed receive state should be reused locally");
        Ensure(GetPrivateField<long>(reused, "Credit") == 3,
            "a reused state must start with the full window before the new receive is reserved");
        Ensure(GetPrivateField<long>(reused, "PendingConsumed") == 0,
            "a reused state must not retain pending credit from the previous stream");
        Ensure(!GetPrivateField<bool>(reused, "Completed"),
            "a reused state must not retain the previous completion marker");
        Ensure(GetPrivateField<object?>(reused, "Next") is null,
            "an active reused state must not retain a pool link");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CompletedReceiveStateShouldNotBeReusedBeforeLateCreditReturns()
    {
        var receiver = new StreamFlowController(4, 4, 1024, maxConcurrentStreams: 1);
        receiver.AcceptReceived(10, 1, 1);
        var retained = GetReceiveState(receiver, 10, 1);

        Ensure(receiver.FlushConsumed(10, 1) == 0,
            "completion must retain a state with unreturned receive credit");
        Ensure(ReferenceEquals(retained, GetReceiveState(receiver, 10, 1)),
            "completion alone must not replace or pool the live receive state");
        try
        {
            receiver.AcceptReceived(11, 1, 1);
            throw new Exception("expected completed receive tombstone capacity exhaustion");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }

        Ensure(receiver.RecordConsumed(10, 1, 1) == 1,
            "the late final credit must be returned exactly once");
        Ensure(GetReceiveStateCount(receiver) == 0,
            "the completed state may leave the dictionary only after its final credit returns");
        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 0,
            "a one-state pool must not report an overflow node");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 1,
            "the removed state should occupy the pool head without an overflow link");
        receiver.AcceptReceived(11, 1, 1);
        Ensure(ReferenceEquals(retained, GetReceiveState(receiver, 11, 1)),
            "only the removed completed state may be reused by the replacement key");
        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 0,
            "renting the sole pool head must leave the overflow count unchanged");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 0,
            "renting the sole pool head must empty the pool");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ReceiveStatePoolShouldRetainAtMostItsBoundedCapacity()
    {
        const int maxConcurrentStreams = 129;
        var receiver = new StreamFlowController(
            streamWindow: 1,
            connectionWindow: maxConcurrentStreams,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: maxConcurrentStreams);
        for (var requestId = 1; requestId <= maxConcurrentStreams; requestId++)
            receiver.AcceptReceived(requestId, 1, 1);

        for (var requestId = 1; requestId <= maxConcurrentStreams; requestId++)
        {
            Ensure(receiver.FlushConsumed(requestId, 1) == 0,
                "completion should retain each exhausted receive state until its final credit returns");
            Ensure(receiver.RecordConsumed(requestId, 1, 1) == 1,
                "each completed state should return its final credit once");
        }

        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 127,
            "a 128-state pool must report the 127 nodes after its head");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 128,
            "receive-state retention must be capped below the negotiated stream limit");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ReturnedReceiveStateShouldRemainReusableWhileEarlierStatesStayActive()
    {
        const int maxConcurrentStreams = 129;
        var receiver = new StreamFlowController(
            streamWindow: 1,
            connectionWindow: maxConcurrentStreams,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: maxConcurrentStreams);
        for (var requestId = 1; requestId < maxConcurrentStreams; requestId++)
            receiver.AcceptReceived(requestId, 1, 1);

        receiver.AcceptReceived(maxConcurrentStreams, 1, 1);
        var churned = GetReceiveState(receiver, maxConcurrentStreams, 1);
        Ensure(receiver.FlushConsumed(maxConcurrentStreams, 1) == 0,
            "the 129th state should await its final credit while the first 128 remain active");
        Ensure(receiver.RecordConsumed(maxConcurrentStreams, 1, 1) == 1,
            "the 129th state should return its final credit and enter the empty pool");
        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 0,
            "a returned churn state must occupy the head without an overflow node");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 1,
            "the returned churn state must be retained even while earlier states stay active");

        receiver.AcceptReceived(maxConcurrentStreams + 1L, 1, 1);
        Ensure(ReferenceEquals(churned, GetReceiveState(receiver, maxConcurrentStreams + 1L, 1)),
            "the next churn stream should reuse the returned state instead of allocating another one");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ReceiveStatePoolOverflowCountShouldTrackNodesAfterHeadAcrossRentAndReturn()
    {
        const int maxConcurrentStreams = 3;
        var receiver = new StreamFlowController(
            streamWindow: 1,
            connectionWindow: maxConcurrentStreams,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: maxConcurrentStreams);
        var initialStates = new object[maxConcurrentStreams];
        for (var requestId = 1; requestId <= maxConcurrentStreams; requestId++)
        {
            receiver.AcceptReceived(requestId, 1, 1);
            initialStates[requestId - 1] = GetReceiveState(receiver, requestId, 1);
        }

        for (var requestId = 1; requestId <= maxConcurrentStreams; requestId++)
        {
            Ensure(receiver.FlushConsumed(requestId, 1) == 0,
                "each exhausted state should await its final credit before pooling");
            Ensure(receiver.RecordConsumed(requestId, 1, 1) == 1,
                "each completed state should return its final credit before pooling");
        }
        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 2,
            "a three-state pool must report two nodes after its head");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 3,
            "all three removed states should be linked in the pool");

        for (var requestId = 4; requestId <= 6; requestId++)
        {
            receiver.AcceptReceived(requestId, 1, 1);
            var expectedState = initialStates[6 - requestId];
            var activeState = GetReceiveState(receiver, requestId, 1);
            Ensure(ReferenceEquals(expectedState, activeState),
                "multi-node rents should pop the pool head in last-returned-first order");
            Ensure(GetPrivateField<object?>(activeState, "Next") is null,
                "a state popped from a multi-node pool must not retain its overflow link");
            var pooledStateCount = 6 - requestId;
            Ensure(
                GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") ==
                    Math.Max(0, pooledStateCount - 1),
                "each multi-node pop should decrement only the overflow-node count");
            Ensure(GetPooledReceiveStateLinkCount(receiver) == pooledStateCount,
                "each rent should remove exactly one state from the pool chain");
        }

        for (var requestId = 4; requestId <= 6; requestId++)
        {
            Ensure(receiver.FlushConsumed(requestId, 1) == 0,
                "each reused state should await its final credit before returning to the pool");
            Ensure(receiver.RecordConsumed(requestId, 1, 1) == 1,
                "each reused state should return its final credit before rejoining the pool");
            var pooledStateCount = requestId - 3;
            Ensure(
                GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") ==
                    Math.Max(0, pooledStateCount - 1),
                "the first return should fill the head and later returns should add overflow nodes");
            Ensure(GetPooledReceiveStateLinkCount(receiver) == pooledStateCount,
                "each return should add exactly one state to the bounded pool chain");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ReceiveStatePoolShouldRespectSmallNegotiatedLimitAcrossChurn()
    {
        const int maxConcurrentStreams = 2;
        const int churnCycles = 8;
        var receiver = new StreamFlowController(
            streamWindow: 1,
            connectionWindow: maxConcurrentStreams,
            maxFramePayloadBytes: 1024,
            maxConcurrentStreams: maxConcurrentStreams);
        Ensure(GetPrivateField<int>(receiver, "_maxPooledReceiveStates") == maxConcurrentStreams,
            "a negotiated limit below the static cap must bound the local receive-state pool");

        object? initialFirst = null;
        object? initialSecond = null;
        for (var cycle = 0; cycle < churnCycles; cycle++)
        {
            var firstRequestId = (cycle * 2L) + 1;
            var secondRequestId = firstRequestId + 1;
            receiver.AcceptReceived(firstRequestId, 1, 1);
            receiver.AcceptReceived(secondRequestId, 1, 1);
            var first = GetReceiveState(receiver, firstRequestId, 1);
            var second = GetReceiveState(receiver, secondRequestId, 1);
            Ensure(!ReferenceEquals(first, second), "two active receive keys must remain distinct");

            if (cycle == 0)
            {
                initialFirst = first;
                initialSecond = second;
            }
            else
            {
                var expectedFirst = initialFirst
                    ?? throw new Exception("initial first receive state was not captured");
                var expectedSecond = initialSecond
                    ?? throw new Exception("initial second receive state was not captured");
                Ensure(
                    (ReferenceEquals(first, expectedFirst) && ReferenceEquals(second, expectedSecond)) ||
                    (ReferenceEquals(first, expectedSecond) && ReferenceEquals(second, expectedFirst)),
                    "small-limit churn must recycle only the two bounded receive-state instances");
            }

            Ensure(receiver.FlushConsumed(firstRequestId, 1) == 0,
                "the first exhausted state should await its final credit");
            Ensure(receiver.RecordConsumed(firstRequestId, 1, 1) == 1,
                "the first final credit should recycle its state");
            Ensure(receiver.FlushConsumed(secondRequestId, 1) == 0,
                "the second exhausted state should await its final credit");
            Ensure(receiver.RecordConsumed(secondRequestId, 1, 1) == 1,
                "the second final credit should recycle its state");
            Ensure(GetReceiveStateCount(receiver) == 0,
                "every churn cycle must remove both completed receive states before pooling");
            Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 1,
                "a two-state pool must report one node after its head");
            Ensure(GetPooledReceiveStateLinkCount(receiver) == maxConcurrentStreams,
                "the free-state chain must remain bounded by the negotiated limit during churn");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task CompleteShouldClearActiveAndPooledReceiveStateReferences()
    {
        var receiver = new StreamFlowController(4, 12, 1024, maxConcurrentStreams: 3);
        receiver.AcceptReceived(21, 1, 2);
        var first = GetReceiveState(receiver, 21, 1);
        receiver.AcceptReceived(22, 1, 2);
        var second = GetReceiveState(receiver, 22, 1);
        receiver.AcceptReceived(23, 1, 1);
        var active = GetReceiveState(receiver, 23, 1);

        receiver.FlushConsumed(21, 1);
        Ensure(receiver.RecordConsumed(21, 1, 2) == 2, "the first state should enter the free pool");
        receiver.FlushConsumed(22, 1);
        Ensure(receiver.RecordConsumed(22, 1, 2) == 2, "the second state should link ahead of the first");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 2,
            "the test must create a multi-node free-state chain before connection completion");

        receiver.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed"));

        Ensure(GetReceiveStateCount(receiver) == 0, "connection completion must clear active receive states");
        Ensure(GetPrivateField<object?>(receiver, "_pooledReceiveStates") is null,
            "connection completion must release the free-state chain root");
        Ensure(GetPrivateField<int>(receiver, "_pooledReceiveStateOverflowCount") == 0,
            "connection completion must reset the free-state overflow count");
        Ensure(GetPooledReceiveStateLinkCount(receiver) == 0,
            "connection completion must leave no reachable free-state references");
        EnsureReceiveStateCleared(first, "first pooled state");
        EnsureReceiveStateCleared(second, "second pooled state");
        EnsureReceiveStateCleared(active, "active state");
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
