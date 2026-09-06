using System.Threading;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowControllerTests
{

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
}
