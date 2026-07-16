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
        manager.CompleteStream(20, 3, exception: null);

        Ensure(dispatcher.CompleteCount == 1, "complete called once");
        Ensure(dispatcher.LastException is SharpLinkException { Code: SharpLinkErrorCode.RemoteError, Message: "boom" }, "error should preserve SharpLinkException");
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
        Ensure(d1.LastException is SharpLinkException { Code: SharpLinkErrorCode.RemoteError, Message: "shutdown" }, "d1 error");
        Ensure(d2.LastException is SharpLinkException { Code: SharpLinkErrorCode.RemoteError, Message: "shutdown" }, "d2 error");
        Ensure(d3.LastException is SharpLinkException { Code: SharpLinkErrorCode.RemoteError, Message: "shutdown" }, "d3 error");
    }

    [Test]
    public void CompleteStreamShouldPreserveSuppliedException()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        var exception = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "transport closed");
        manager.Register(30, dispatcher);

        manager.CompleteStream(30, exception);

        Ensure(ReferenceEquals(exception, dispatcher.LastException), "manager should pass through supplied exception");
    }

    [Test]
    public async Task SlowConsumerShouldReceiveResourceExhaustedAt4096BufferedElements()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent();
        var writer = new ArrayBufferWriter<byte>();
        RpcCodec.Serialize(42, writer);
        var payload = new ReadOnlySequence<byte>(writer.WrittenMemory);

        for (var index = 0; index <= 4096; index++)
            await dispatcher.DispatchAsync(payload);

        var enumerator = dispatcher.GetAsyncEnumerator();
        var received = 0;
        try
        {
            while (await enumerator.MoveNextAsync())
                received++;
            throw new Exception("expected stream ResourceExhausted");
        }
        catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
        {
        }

        Ensure(received == 4096, "dispatcher should stop growth at 4096 buffered elements");
    }

    [Test]
    public async Task FlowControlledDispatcherShouldReturnBytesOnlyAfterConsumption()
    {
        var accepted = 0;
        var consumed = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent();
        manager.Register(40, 2, dispatcher);
        var writer = new ArrayBufferWriter<byte>();
        RpcCodec.Serialize(42, writer);

        await manager.DispatchChunkAsync(40, 2, new ReadOnlySequence<byte>(writer.WrittenMemory));
        Ensure(accepted == writer.WrittenCount, "encoded bytes should be admitted before decode");
        Ensure(consumed == 0, "queued bytes must not be returned before consumption");

        var enumerator = dispatcher.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync(), "stream item should be available");
        Ensure(consumed == writer.WrittenCount, "consumer should return the exact encoded byte count");
        dispatcher.Complete(exception: null);
        await enumerator.DisposeAsync();
    }

    [Test]
    public async Task UnknownStreamDataShouldBeDroppedWithoutRecreatingDispatcher()
    {
        var manager = new StreamManager();
        await manager.DispatchChunkAsync(404, 7, new ReadOnlySequence<byte>(new byte[] { 1 }));
        await manager.DispatchChunkAsync(404, 7, new ReadOnlySequence<byte>(new byte[] { 2 }));
        Ensure(manager.DroppedStreamFrames == 2, "late stream data should be counted and dropped");
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
        public Exception? LastException { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            DispatchCount++;
            LastPayloadLength = payload.Length;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            Complete(isError
                ? new SharpLinkException(
                    SharpLinkErrorCode.RemoteError,
                    string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage)
                : null);
        }

        public void Complete(Exception? exception)
        {
            CompleteCount++;
            LastException = exception;
        }
    }
}
