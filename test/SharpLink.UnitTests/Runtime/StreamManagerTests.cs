using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class StreamManagerTests
{
    private static readonly IRpcCodecProvider SCodecs =
        new SharpLinkRuntimeContextBuilder().Build().Codecs;

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
        Ensure(manager.ActiveStreamCount == 0, "all registered streams should be removed");
    }

    [Test]
    public async Task CompleteRequestStreamsShouldRetireOnlyTheTargetRequest()
    {
        var manager = new StreamManager();
        var target1 = new RecordingDispatcher();
        var target2 = new RecordingDispatcher();
        var unrelated = new RecordingDispatcher();
        var exception = new OperationCanceledException("handler returned early");
        manager.Register(10, 1, target1);
        manager.Register(10, 2, target2);
        manager.Register(11, 1, unrelated);

        manager.CompleteRequestStreams(10, exception);

        Ensure(target1.CompleteCount == 1, "first target stream completed");
        Ensure(target2.CompleteCount == 1, "second target stream completed");
        Ensure(ReferenceEquals(exception, target1.LastException), "first target preserves exception");
        Ensure(ReferenceEquals(exception, target2.LastException), "second target preserves exception");
        Ensure(unrelated.CompleteCount == 0, "unrelated request remains active");
        Ensure(manager.ActiveStreamCount == 1, "only the unrelated request remains registered");

        await manager.DispatchChunkAsync(10, 1, new ReadOnlySequence<byte>(new byte[] { 1 }));
        Ensure(target1.DispatchCount == 0, "late target frames are dropped after completion");
        Ensure(manager.DroppedStreamFrames == 1, "late target frame is counted as dropped");

        manager.CompleteRequestStreams(10, exception);
        Ensure(target1.CompleteCount == 1 && target2.CompleteCount == 1,
            "request completion is idempotent");
    }

    [Test]
    public void RegisterAfterCompleteAllShouldCompleteWithoutPublishingAnActiveStream()
    {
        var manager = new StreamManager();
        var exception = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "session closed");
        manager.CompleteAll(exception);
        var dispatcher = new RecordingDispatcher();

        manager.Register(3, 1, dispatcher);

        Ensure(dispatcher.CompleteCount == 1, "late dispatcher should be completed once");
        Ensure(ReferenceEquals(exception, dispatcher.LastException), "late dispatcher should preserve terminal error");
        Ensure(manager.ActiveStreamCount == 0, "late registration must not increment active streams");
    }

    [Test]
    public async Task RegisterRacingCompleteAllShouldNotLeaveAnOrphanedStream()
    {
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var manager = new StreamManager();
            var dispatcher = new RecordingDispatcher();
            using var start = new ManualResetEventSlim();
            var register = Task.Run(() =>
            {
                start.Wait();
                manager.Register(iteration, dispatcher);
            });
            var complete = Task.Run(() =>
            {
                start.Wait();
                manager.CompleteAll(new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed,
                    "session closed"));
            });

            start.Set();
            await Task.WhenAll(register, complete);
            Ensure(dispatcher.CompleteCount == 1, "racing dispatcher should be completed once");
            Ensure(manager.ActiveStreamCount == 0, "racing registration must be drained");
        }
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
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(codecProvider: SCodecs);
        var writer = new ArrayBufferWriter<byte>();
        SCodecs.GetCodec<int>().Serialize(42, writer);
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
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(codecProvider: SCodecs);
        manager.Register(40, 2, dispatcher);
        var writer = new ArrayBufferWriter<byte>();
        SCodecs.GetCodec<int>().Serialize(42, writer);

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

    [Test]
    public async Task PreAdmissionCapacityDropShouldReturnAcceptedReceiveCredit()
    {
        var accepted = 0;
        var consumed = 0;
        var capacityExceeded = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            50,
            1,
            buffers,
            _ => false,
            _ => throw new InvalidOperationException("No bytes were reserved."),
            () => capacityExceeded++);

        await manager.DispatchChunkAsync(
            50,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));

        Ensure(accepted == 4, "pre-admission bytes accepted");
        Ensure(consumed == 4, "dropped pre-admission bytes returned as receive credit");
        Ensure(capacityExceeded == 1, "pre-admission capacity callback");
        manager.CompleteRequestStreams(50, exception: null);
        Ensure(manager.ActiveStreamCount == 0, "capacity-dropped stream reclaimed");
    }

    [Test]
    public async Task CompletedPreAdmissionStreamShouldRetireAfterDispatcherAttach()
    {
        var released = 0;
        var completed = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            (_, _) => completed++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            51,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            51,
            1,
            new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 }));
        manager.CompleteStream(51, 1, exception: null);
        var dispatcher = new RecordingDispatcher();

        manager.Register(51, 1, dispatcher);

        Ensure(dispatcher.DispatchCount == 1, "buffered pre-admission item dispatched");
        Ensure(dispatcher.CompleteCount == 1, "early completion forwarded on attach");
        Ensure(released == 3, "buffered pre-admission bytes released");
        Ensure(completed == 1, "stream completion callback invoked once");
        Ensure(manager.ActiveStreamCount == 0, "completed pre-admission stream reclaimed");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreAdmissionCompletionDuringRetentionShouldReturnReceiveCredit(bool compressed)
    {
        const long requestId = 56;
        const ushort streamId = 1;
        var accepted = 0;
        var consumed = 0;
        var released = 0;
        var decoded = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            _ =>
            {
                manager.CompleteStream(requestId, streamId, exception: null);
                return true;
            },
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."),
            _ =>
            {
                decoded++;
                throw new InvalidOperationException("A completed pre-admission stream must not decode its frame.");
            });

        if (compressed)
        {
            Ensure(manager.TryDispatchPreAdmissionCompressed(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[] { 4, 5, 6 }),
                originalByteCount: 17,
                out var dispatch),
                "compressed pre-admission frame intercepted");
            await dispatch;
        }
        else
        {
            await manager.DispatchChunkAsync(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));
        }

        var expectedCredit = compressed ? 17 : 4;
        Ensure(accepted == expectedCredit && consumed == expectedCredit,
            "completion race returns the exact accepted receive credit");
        Ensure(released == (compressed ? 3 : 4), "completion race releases retained wire bytes");
        Ensure(decoded == 0, "completed compressed frame is discarded before decode");
        manager.Register(requestId, streamId, new RecordingDispatcher());
        Ensure(manager.ActiveStreamCount == 0, "completed pre-admission stream reclaimed after attach");
    }

    [Test]
    public async Task RejectedStreamDrainerShouldReturnCreditAndRetireOnComplete()
    {
        var accepted = 0;
        var consumed = 0;
        var completed = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            (_, _) => completed++);
        manager.DrainRejectedRequestStreams(52, 1);

        await manager.DispatchChunkAsync(
            52,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));
        Ensure(manager.TryDispatchPreAdmissionCompressed(
            52,
            1,
            new ReadOnlySequence<byte>(new byte[] { 9, 9 }),
            originalByteCount: 11,
            out var compressedDispatch),
            "discarding stream should intercept compressed frames before decode");
        await compressedDispatch;
        manager.CompleteStream(52, 1, exception: null);

        Ensure(accepted == 14 && consumed == 14,
            "discarded raw and compressed bytes return exact original credit");
        Ensure(completed == 1, "discarded stream completion callback");
        Ensure(manager.ActiveStreamCount == 0, "discarded stream reclaimed");
    }

    [Test]
    public async Task FailureDrainerShouldNotReplaceAnAttachedGeneratedDispatcher()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        manager.Register(55, 1, dispatcher);

        manager.DrainRejectedRequestStreams(55, 1);
        await manager.DispatchChunkAsync(
            55,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2 }));

        Ensure(dispatcher.DispatchCount == 1, "existing generated dispatcher remains active");
        Ensure(manager.ActiveStreamCount == 1, "ignored drainer does not change active accounting");
        manager.CompleteStream(55, 1, exception: null);
        Ensure(manager.ActiveStreamCount == 0, "existing dispatcher reclaimed once");
    }

    [Test]
    public async Task RejectedQueuedCompressedStreamShouldNotInvokeDecoder()
    {
        var accepted = 0;
        var consumed = 0;
        var released = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            53,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."),
            _ => throw new InvalidOperationException("Rejected compressed frames must not be decoded."));
        Ensure(manager.TryDispatchPreAdmissionCompressed(
            53,
            1,
            new ReadOnlySequence<byte>(new byte[] { 4, 5, 6, 7 }),
            originalByteCount: 32,
            out var queuedDispatch),
            "compressed pre-admission frame intercepted");
        await queuedDispatch;

        manager.DrainRejectedRequestStreams(53, 1);
        await manager.DispatchChunkAsync(
            53,
            1,
            new ReadOnlySequence<byte>(new byte[] { 8, 9, 10 }));
        manager.CompleteStream(53, 1, exception: null);

        Ensure(accepted == 35 && consumed == 35,
            "queued rejection returns buffered and future frame credit");
        Ensure(released == 4, "queued rejected compressed wire bytes released");
        Ensure(manager.ActiveStreamCount == 0, "queued rejected compressed stream reclaimed");
    }

    [Test]
    public async Task ReplayFailureShouldReleaseEveryRemainingPreAdmissionItem()
    {
        var accepted = 0;
        var consumed = 0;
        var retained = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            54,
            1,
            buffers,
            bytes =>
            {
                retained += bytes;
                return true;
            },
            bytes => retained -= bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        for (var value = 1; value <= 3; value++)
        {
            await manager.DispatchChunkAsync(
                54,
                1,
                new ReadOnlySequence<byte>(new byte[] { checked((byte)value) }));
        }

        try
        {
            manager.Register(54, 1, new ThrowingReplayDispatcher());
            throw new Exception("expected replay failure");
        }
        catch (InvalidDataException)
        {
        }

        Ensure(retained == 0, "failed replay should release every retained owner");
        Ensure(accepted == 3 && consumed == 3,
            "failed replay should return credit for the failed and unvisited items");
        manager.DrainRejectedRequestStreams(54, 1);
        await manager.DispatchChunkAsync(
            54,
            1,
            new ReadOnlySequence<byte>(new byte[] { 4 }));
        manager.CompleteStream(54, 1, exception: null);
        Ensure(accepted == 4 && consumed == 4,
            "failed replay should recover in place as a credit-returning drainer");
        Ensure(manager.ActiveStreamCount == 0, "failed replay stream reclaimed");
    }

    [Test]
    public async Task PreAdmissionAttachShouldNotBlockOnAsynchronousReplay()
    {
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            57,
            1,
            buffers,
            _ => true,
            _ => { },
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            57,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        var dispatcher = new OrderedReplayDispatcher();

        var registration = Task.Run(() => manager.Register(57, 1, dispatcher));
        await dispatcher.FirstEntered.WaitAsync(TimeSpan.FromSeconds(1));
        var returnedBeforeRelease = false;
        try
        {
            await registration.WaitAsync(TimeSpan.FromMilliseconds(200));
            returnedBeforeRelease = true;
        }
        catch (TimeoutException)
        {
        }

        ValueTask liveDispatch = default;
        if (returnedBeforeRelease)
        {
            liveDispatch = manager.DispatchChunkAsync(
                57,
                1,
                new ReadOnlySequence<byte>(new byte[] { 2 }));
            Ensure(liveDispatch.IsCompletedSuccessfully,
                "a live frame is retained without blocking the transport reader");
            Ensure(dispatcher.EnteredValues.SequenceEqual([(byte)1]),
                "a live frame must not overtake retained replay");
        }
        dispatcher.ReleaseFirst();
        await registration.WaitAsync(TimeSpan.FromSeconds(1));
        if (returnedBeforeRelease)
            await dispatcher.SecondEntered.WaitAsync(TimeSpan.FromSeconds(1));

        Ensure(returnedBeforeRelease,
            "dispatcher registration must not synchronously wait for asynchronous replay");
        Ensure(dispatcher.EnteredValues.SequenceEqual([(byte)1, (byte)2]),
            "retained and live frames preserve wire order");
        manager.CompleteStream(57, 1, exception: null);
    }

    [Test]
    public async Task PreAdmissionAttachCallbacksShouldRunOutsideRequestRegistryLock()
    {
        const long requestId = 58;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            _ => true,
            _ => { },
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        var dispatcher = new ReentrantConfigurationDispatcher(manager, requestId);

        await Task.Run(() => manager.Register(requestId, 1, dispatcher))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(!dispatcher.RegistryLockWasHeld,
            "dispatcher callbacks must execute without the request registry lock");
        manager.CompleteRequestStreams(requestId, exception: null);
    }

    [Test]
    public async Task CompletionDuringAsynchronousReplayShouldFollowRetainedFrames()
    {
        var released = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            59,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            59,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        var dispatcher = new OrderedReplayDispatcher();

        manager.Register(59, 1, dispatcher);
        await dispatcher.FirstEntered.WaitAsync(TimeSpan.FromSeconds(1));
        manager.CompleteStream(59, 1, exception: null);

        Ensure(dispatcher.CompleteCount == 0,
            "completion must wait until retained replay exits");
        Ensure(manager.ActiveStreamCount == 0,
            "the completed registry entry retires while replay owns its lease");
        dispatcher.ReleaseFirst();
        await dispatcher.Completed.WaitAsync(TimeSpan.FromSeconds(1));

        Ensure(dispatcher.CompleteCount == 1,
            "completion is forwarded once after replay");
        Ensure(released == 1,
            "retained storage is released before completion finishes");
    }

    [Test]
    public async Task LocalCancellationShouldFlushOnlyAfterAcquiredDispatchesDrain()
    {
        var events = new List<string>();
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            null,
            null,
            (_, _) => events.Add("credit-flushed"));
        var dispatcher = new GatedDispatcher(events);
        manager.Register(50, dispatcher);

        var dispatch = manager.DispatchChunkAsync(
            50,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(1));
        var completion = manager.CompleteStreamAfterDispatchesAsync(
            50,
            0,
            new OperationCanceledException());

        Ensure(!completion.IsCompleted, "local completion must wait for the acquired dispatch");
        Ensure(events.SequenceEqual(["dispatch-entered", "dispatcher-completed"]),
            "receive credit must not flush before the acquired dispatch exits");

        dispatcher.Release();
        await dispatch;
        await completion;
        Ensure(events.SequenceEqual([
                "dispatch-entered",
                "dispatcher-completed",
                "dispatch-released",
                "credit-flushed"
            ]),
            "the final credit flush must follow the last acquired dispatch");
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

    private sealed class GatedDispatcher(List<string> events) : IStreamDispatcher
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            events.Add("dispatch-entered");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            events.Add("dispatch-released");
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError ? new Exception(errorMessage) : null);

        public void Complete(Exception? exception)
        {
            _ = exception;
            events.Add("dispatcher-completed");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingReplayDispatcher : IStreamConsumptionAwareDispatcher
    {
        private Action<long, ushort, int>? _bytesConsumed;
        private long _requestId;
        private ushort _streamId;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, checked((int)payload.Length));

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _ = payload;
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            throw new InvalidDataException("Injected pre-admission replay failure.");
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long requestId,
            ushort streamId)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
        }
    }

    private sealed class OrderedReplayDispatcher : IStreamDispatcher
    {
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dispatchCount;
        private int _completeCount;

        internal Task FirstEntered => _firstEntered.Task;
        internal Task SecondEntered => _secondEntered.Task;
        internal Task Completed => _completed.Task;
        internal List<byte> EnteredValues { get; } = [];
        internal int CompleteCount => Volatile.Read(ref _completeCount);

        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            var value = payload.FirstSpan[0];
            EnteredValues.Add(value);
            var dispatchCount = Interlocked.Increment(ref _dispatchCount);
            if (dispatchCount == 1)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.ConfigureAwait(false);
            }
            else if (dispatchCount == 2)
            {
                _secondEntered.TrySetResult();
            }
        }

        public void Complete(bool isError, string? errorMessage)
        {
            Complete(isError ? new Exception(errorMessage) : null);
        }

        public void Complete(Exception? exception)
        {
            _ = exception;
            Interlocked.Increment(ref _completeCount);
            _completed.TrySetResult();
        }

        internal void ReleaseFirst() => _releaseFirst.TrySetResult();
    }

    private sealed class ReentrantConfigurationDispatcher(
        StreamManager manager,
        long requestId) : IStreamConsumptionAwareDispatcher
    {
        internal bool RegistryLockWasHeld { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => ValueTask.CompletedTask;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => ValueTask.CompletedTask;

        public void Complete(bool isError, string? errorMessage)
        {
        }

        public void Complete(Exception? exception)
        {
        }

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long attachedRequestId,
            ushort streamId)
        {
            _ = callback;
            _ = attachedRequestId;
            _ = streamId;
            var nestedRegistration = Task.Run(() =>
                manager.Register(requestId, 2, new RecordingDispatcher()));
            RegistryLockWasHeld = !nestedRegistration.Wait(TimeSpan.FromMilliseconds(200));
        }
    }
}
