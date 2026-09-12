namespace SharpLink.UnitTests.Runtime;

public class PreAdmissionStreamCompletionLeaseTests
{
    [Test]
    public void CompletionExceptionDuringRetainedAttachShouldPreserveOriginalFailureAndLeaseBalance()
    {
        const long requestId = 30408;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => counters.Accepted += bytes,
            (_, _, bytes) => counters.Consumed += bytes,
            (_, _) => counters.Completed++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        manager.CompletePeerStream(requestId, streamId, exception: null);
        Ensure(manager.ActiveStreamCount == 1 && counters.Completed == 1,
            "peer terminal should publish receive completion while retaining the OneWay route");

        var expected = new InvalidOperationException("completion exploded");
        var typed = new ThrowingCompletionDispatcher(expected);
        Exception? observed = null;
        try
        {
            manager.Register(requestId, streamId, typed);
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        Ensure(ReferenceEquals(observed, expected),
            "attachment completion must preserve the dispatcher's original completion failure");
        Ensure(manager.ActiveStreamCount == 1,
            "completion failure must leave the retained stable route available for local cleanup");

        manager.AbandonExistingRequestStreams(requestId, 1);

        Ensure(manager.ActiveStreamCount == 0,
            "local cleanup after peer terminal should retire the retained route without lease underflow");
        Ensure(counters.Completed == 1,
            "cleanup after the completion exception must not republish receive terminal");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Counters
    {
        internal int Accepted;
        internal int Consumed;
        internal int Completed;
    }

    private sealed class ThrowingCompletionDispatcher(Exception completionFailure) : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
            throw completionFailure;
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            throw completionFailure;
        }
    }
}
