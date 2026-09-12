namespace SharpLink.UnitTests.Runtime;

public class InboundStreamRouteCapacityTests
{
    [Test]
    public void RepeatedLocalCompletionWithoutPeerTerminalShouldRemainBoundedByRouteQuota()
    {
        const int maxActiveStreams = 4;
        var capacityExceeded = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            streamCompleted: null,
            maxActiveStreams,
            _ => capacityExceeded++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());

        for (var index = 0; index < maxActiveStreams; index++)
        {
            var requestId = 50_000L + index;
            manager.ReservePreAdmissionStreams(
                requestId,
                1,
                buffers,
                static _ => true,
                static _ => { },
                static () => { },
                retainUntilLocalCompletion: true);
            manager.AbandonExistingRequestStreams(requestId, 1);
        }

        Ensure(manager.ActiveStreamCount == maxActiveStreams,
            "zero-data abandoned routes must remain charged while peer terminal is missing");

        SharpLinkException? observed = null;
        try
        {
            manager.ReservePreAdmissionStreams(
                60_000,
                1,
                buffers,
                static _ => true,
                static _ => { },
                static () => { },
                retainUntilLocalCompletion: true);
        }
        catch (SharpLinkException exception)
        {
            observed = exception;
        }

        Ensure(observed?.Code == SharpLinkErrorCode.ResourceExhausted,
            "the next zero-data route must fail once the per-connection route quota is full");
        Ensure(capacityExceeded == 1,
            "route quota exhaustion should signal the owning session exactly once");
        Ensure(manager.ActiveStreamCount == maxActiveStreams,
            "failed registration must not grow retained route state past the quota");

        manager.CompletePeerStream(50_000, 1, exception: null);
        Ensure(manager.ActiveStreamCount == maxActiveStreams - 1,
            "peer terminal should release one retained route slot");

        manager.ReservePreAdmissionStreams(
            60_001,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);
        Ensure(manager.ActiveStreamCount == maxActiveStreams,
            "a released route slot should be reusable");

        manager.CompleteAll(new OperationCanceledException("test cleanup"));
        Ensure(manager.ActiveStreamCount == 0,
            "session teardown should release all retained route slots");
    }

    [Test]
    public void TypedAttachmentShouldReuseExistingPreAdmissionRouteSlot()
    {
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            streamCompleted: null,
            maxActiveStreams: 1,
            activeStreamCapacityExceeded: static _ =>
                throw new Exception("typed attachment must not consume a second route slot"));
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());

        manager.ReservePreAdmissionStreams(
            70_000,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);
        var typed = PooledAsyncStreamDispatcher<int>.Rent(default, RpcSessionTestFixture.RuntimeContext.Codecs);
        manager.Register(70_000, 1, typed);

        Ensure(manager.ActiveStreamCount == 1,
            "typed attachment should replace the pre-admission child without double charging quota");

        manager.CompletePeerStream(70_000, 1, exception: null);
        manager.AbandonExistingRequestStreams(70_000, 1);
        Ensure(manager.ActiveStreamCount == 0,
            "peer and local completion should release the reused route slot");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
