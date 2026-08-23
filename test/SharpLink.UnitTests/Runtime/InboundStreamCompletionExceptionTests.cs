namespace SharpLink.UnitTests.Runtime;

public class InboundStreamCompletionExceptionTests
{
    [Test]
    public void CompletedAttachShouldReleaseChildLeaseOnceWhenDispatcherCompleteThrows()
    {
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var route = new PreAdmissionStreamDispatcher(
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);
        route.Complete(new InvalidOperationException("route completed before typed attach"));

        var dispatcher = new ThrowingCompleteDispatcher();
        Ensure(route.TryBeginAttach(dispatcher, out var alreadyCompleted),
            "completed retained route should still accept typed attachment");
        Ensure(!alreadyCompleted,
            "OneWay-retained completion should be delivered through the attached dispatcher");

        Exception? observed = null;
        try
        {
            route.FinishAttach(dispatcher);
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        Ensure(ReferenceEquals(observed, dispatcher.CompletionException),
            "the dispatcher completion failure must not be replaced by a child-lease underflow");
        Ensure(dispatcher.CompleteCallCount == 1,
            "failed completion should not be delivered to the dispatcher twice");

        route.Abandon(out _);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ThrowingCompleteDispatcher : IStreamDispatcher
    {
        internal Exception CompletionException { get; } =
            new InvalidOperationException("typed completion failed");

        internal int CompleteCallCount { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
            Complete(exception: null);
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            CompleteCallCount++;
            throw CompletionException;
        }
    }
}
