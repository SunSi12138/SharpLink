namespace SharpLink.UnitTests.Runtime;

public class StreamManagerTests
{
    [Test]
    public async Task DispatchChunkShouldReachRegisteredDefaultStream()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        manager.Register(10, dispatcher);

        ReadOnlySequence<byte> payload = new([1, 2, 3]);
        await manager.DispatchChunkAsync(10, payload);

        Ensure(dispatcher.DispatchCount == 1, "dispatch count");
        Ensure(dispatcher.LastPayloadLength == 3, "payload length");
    }

    [Test]
    public async Task DispatchChunkShouldRespectStreamId()
    {
        var manager = new StreamManager();
        var stream1 = new RecordingDispatcher();
        var stream2 = new RecordingDispatcher();
        manager.Register(10, 1, stream1);
        manager.Register(10, 2, stream2);

        ReadOnlySequence<byte> payload = new([9]);
        await manager.DispatchChunkAsync(10, 2, payload);

        Ensure(stream1.DispatchCount == 0, "stream1 should not receive payload");
        Ensure(stream2.DispatchCount == 1, "stream2 should receive payload");
    }

    [Test]
    public void CompleteStreamShouldCompleteAndUnregister()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        manager.Register(20, 3, dispatcher);

        manager.CompleteStream(20, 3, true, "boom");
        manager.CompleteStream(20, 3, false, null);

        Ensure(dispatcher.CompleteCount == 1, "complete called once");
        Ensure(dispatcher.LastIsError, "isError");
        Ensure(dispatcher.LastErrorMessage == "boom", "error message");
    }

    [Test]
    public void CompleteAllShouldCompleteEveryRegisteredDispatcher()
    {
        var manager = new StreamManager();
        var d1 = new RecordingDispatcher();
        var d2 = new RecordingDispatcher();
        var d3 = new RecordingDispatcher();

        manager.Register(1, d1);
        manager.Register(2, 1, d2);
        manager.Register(2, 2, d3);

        manager.CompleteAll(true, "shutdown");

        Ensure(d1.CompleteCount == 1, "d1 completed");
        Ensure(d2.CompleteCount == 1, "d2 completed");
        Ensure(d3.CompleteCount == 1, "d3 completed");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class RecordingDispatcher : IStreamDispatcher
    {
        public int DispatchCount { get; private set; }
        public long LastPayloadLength { get; private set; }
        public int CompleteCount { get; private set; }
        public bool LastIsError { get; private set; }
        public string? LastErrorMessage { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            DispatchCount++;
            LastPayloadLength = payload.Length;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            CompleteCount++;
            LastIsError = isError;
            LastErrorMessage = errorMessage;
        }
    }
}
