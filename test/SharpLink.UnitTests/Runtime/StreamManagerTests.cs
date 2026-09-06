using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);

    private static IRpcCodecProvider SCodecs => RpcSessionTestFixture.RuntimeContext.Codecs;

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
    public async Task CompleteAllShouldCloseLookupBeforeTheLastDispatchLeaseDrains()
    {
        var events = new List<string>();
        var manager = new StreamManager();
        var dispatcher = new GatedDispatcher(events);
        manager.Register(51, dispatcher);

        var activeDispatch = manager.DispatchChunkAsync(
            51,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await dispatcher.Entered.WaitAsync(RaceCoordinationTimeout);

        manager.CompleteAll(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "session closed"));
        manager.AssertAccountingInvariant();
        Ensure(manager.ActiveStreamCount == 0,
            "business-stream completion must retire its count before an older dispatch finishes");
        Ensure(!activeDispatch.IsCompleted,
            "the dispatch lease acquired before CompleteAll must stay valid until it releases");

        var lateDispatch = manager.DispatchChunkAsync(
            51,
            new ReadOnlySequence<byte>(new byte[] { 2 }));
        Ensure(lateDispatch.IsCompletedSuccessfully,
            "Close must reject a post-termination lookup without waiting for the old dispatch");
        Ensure(events.SequenceEqual(["dispatch-entered", "dispatcher-completed"]),
            "CompleteAll must complete the dispatcher once without running a late dispatch");

        dispatcher.Release();
        await activeDispatch;
        manager.AssertAccountingInvariant();
        Ensure(events.SequenceEqual([
                "dispatch-entered",
                "dispatcher-completed",
                "dispatch-released"
            ]),
            "the old dispatch releases after completion while the new lookup remains blocked");
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
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
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
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
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
        await dispatcher.Entered.WaitAsync(RaceCoordinationTimeout);
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

    [Test]
    public async Task DetachBeforeWaitShouldCompleteSynchronouslyWithoutLostWakeup()
    {
        var manager = new StreamManager();
        var dispatcher = new CapturingLeaseDispatcher();
        manager.Register(60, dispatcher);
        var state = dispatcher.DispatchState;

        manager.Unregister(60);

        var detached = state.WaitForDetachedAsync(CancellationToken.None);
        Ensure(detached.IsCompletedSuccessfully,
            "an already-detached entry must not wait for a new completion path");
        await detached;
        Ensure(dispatcher.DispatchesDrainedCount == 1,
            "the detached entry must notify its lease exactly once");
    }

    [Test]
    public async Task DetachWaitShouldCompleteEveryRegisteredWaiterOnce()
    {
        var manager = new StreamManager();
        var dispatcher = new CapturingLeaseDispatcher();
        manager.Register(61, dispatcher);
        var state = dispatcher.DispatchState;

        var first = state.WaitForDetachedAsync(CancellationToken.None).AsTask();
        var second = state.WaitForDetachedAsync(CancellationToken.None).AsTask();
        Ensure(!first.IsCompleted && !second.IsCompleted,
            "registered waiters must remain pending until terminal detach");
        Ensure(ReferenceEquals(first, second),
            "every waiter for one entry must share the same lazy detach completion");

        manager.Unregister(61);

        await Task.WhenAll(first, second).WaitAsync(RaceCoordinationTimeout);
        Ensure(dispatcher.DispatchesDrainedCount == 1,
            "one detach transition must notify the dispatcher lease once");
    }

    [Test]
    public async Task DetachRacingWaiterRegistrationShouldNotLoseWakeup()
    {
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var manager = new StreamManager();
            var dispatcher = new CapturingLeaseDispatcher();
            var requestId = iteration + 2000;
            manager.Register(requestId, dispatcher);
            var state = dispatcher.DispatchState;
            using var start = new ManualResetEventSlim();
            var wait = LongRunningTestWorker.RunAsync(async () =>
            {
                start.Wait();
                await state.WaitForDetachedAsync(CancellationToken.None);
            });
            var detach = LongRunningTestWorker.Run(() =>
            {
                start.Wait();
                manager.Unregister(requestId);
            });
            try
            {
                start.Set();
                await Task.WhenAll(wait, detach).WaitAsync(RaceCoordinationTimeout);
                Ensure(state.IsDetached,
                    "the detach/register race must publish a terminal completion to its waiter");
                Ensure(dispatcher.DispatchesDrainedCount == 1,
                    "the detach/register race must retain one dispatcher-drained notification");
            }
            finally
            {
                start.Set();
                await LongRunningTestWorker.JoinAsync(wait, RaceCoordinationTimeout);
                await LongRunningTestWorker.JoinAsync(detach, RaceCoordinationTimeout);
            }
        }
    }

    [Test]
    public async Task DetachWaitCancellationShouldNotPreventLaterDetach()
    {
        var manager = new StreamManager();
        var dispatcher = new CapturingLeaseDispatcher();
        manager.Register(62, dispatcher);
        var state = dispatcher.DispatchState;
        using var cancellation = new CancellationTokenSource();
        var waiting = state.WaitForDetachedAsync(cancellation.Token).AsTask();

        cancellation.Cancel();

        try
        {
            await waiting;
            throw new Exception("expected detach wait cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        manager.Unregister(62);
        await state.WaitForDetachedAsync(CancellationToken.None);
        Ensure(state.IsDetached,
            "cancelling one waiter must not change the entry terminal detach state");
    }

    [Test]
    public async Task DetachAndCancellationRaceShouldNeverLoseWakeupOrDoubleSignal()
    {
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var manager = new StreamManager();
            var dispatcher = new CapturingLeaseDispatcher();
            var requestId = iteration + 1000;
            manager.Register(requestId, dispatcher);
            var state = dispatcher.DispatchState;
            using var cancellation = new CancellationTokenSource();
            using var start = new ManualResetEventSlim();
            var waiting = state.WaitForDetachedAsync(cancellation.Token).AsTask();
            var cancel = Task.Run(() =>
            {
                start.Wait();
                cancellation.Cancel();
            });
            var detach = Task.Run(() =>
            {
                start.Wait();
                manager.Unregister(requestId);
            });

            start.Set();
            try
            {
                await waiting.WaitAsync(RaceCoordinationTimeout);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            await Task.WhenAll(cancel, detach).WaitAsync(RaceCoordinationTimeout);
            Ensure(state.IsDetached,
                "the detach winner must publish terminal state despite cancellation racing it");
            Ensure(dispatcher.DispatchesDrainedCount == 1,
                "the detach race must retain exactly one dispatcher-drained notification");
        }
    }

    [Test]
    public async Task DetachCompletionShouldFollowTheFinalCreditCallback()
    {
        var events = new List<string>();
        var creditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCredit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            null,
            null,
            (_, _) =>
            {
                events.Add("credit-enqueued");
                creditEntered.TrySetResult();
                releaseCredit.Task.GetAwaiter().GetResult();
            });
        var dispatcher = new CapturingLeaseDispatcher();
        manager.Register(63, dispatcher);
        var detached = dispatcher.DispatchState.WaitForDetachedAsync(CancellationToken.None).AsTask();

        var unregister = LongRunningTestWorker.Run(() => manager.Unregister(63));
        try
        {
            await creditEntered.Task.WaitAsync(RaceCoordinationTimeout);
            Ensure(!detached.IsCompleted,
                "detach must remain unpublished while the final receive-credit callback is active");

            releaseCredit.TrySetResult();
            await unregister.WaitAsync(RaceCoordinationTimeout);
            await detached.WaitAsync(RaceCoordinationTimeout);
            events.Add("detached");
            Ensure(events.SequenceEqual(["credit-enqueued", "detached"]),
                "the final receive-credit callback must complete before detach is observable");
        }
        finally
        {
            releaseCredit.TrySetResult();
            await LongRunningTestWorker.JoinAsync(unregister, RaceCoordinationTimeout);
        }
    }

    [Test]
    public async Task DetachShouldNotReturnAnActiveDispatcherLeaseBeforeItsLastRelease()
    {
        var manager = new StreamManager();
        var dispatcher = new GatedLeaseDispatcher();
        manager.Register(64, dispatcher);
        var state = dispatcher.DispatchState;

        var dispatch = manager.DispatchChunkAsync(
            64,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await dispatcher.DispatchEntered.WaitAsync(RaceCoordinationTimeout);

        manager.Unregister(64);
        await state.WaitForDetachedAsync(CancellationToken.None);
        Ensure(dispatcher.DispatchesDrainedCount == 0,
            "detach alone must not return a lease while an acquired dispatch remains active");

        dispatcher.ReleaseDispatch();
        await dispatch.WaitAsync(RaceCoordinationTimeout);
        await dispatcher.DispatchesDrained.WaitAsync(RaceCoordinationTimeout);
        Ensure(dispatcher.DispatchesDrainedCount == 1,
            "the final active dispatch release must return the detached dispatcher lease once");
    }

    [Test]
    public async Task DispatchDrainAndDetachWaitsShouldRemainIndependentWhenSharingCompletions()
    {
        var finalCreditEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalCredit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            null,
            null,
            (_, _) =>
            {
                finalCreditEntered.TrySetResult();
                releaseFinalCredit.Task.GetAwaiter().GetResult();
            });
        var dispatcher = new GatedLeaseDispatcher();
        manager.Register(65, dispatcher);
        var state = dispatcher.DispatchState;

        var dispatch = manager.DispatchChunkAsync(
            65,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await dispatcher.DispatchEntered.WaitAsync(RaceCoordinationTimeout);

        var dispatchDrain = manager.CompleteStreamAfterDispatchesAsync(
            65,
            0,
            new OperationCanceledException()).AsTask();
        var stateDispatchDrain = state.WaitForDispatchesDrainedAsync().AsTask();
        var detached = state.WaitForDetachedAsync(CancellationToken.None).AsTask();
        Ensure(!dispatchDrain.IsCompleted && !stateDispatchDrain.IsCompleted && !detached.IsCompleted,
            "the distinct drain and detach signals must both remain pending before the acquired dispatch releases");

        dispatcher.ReleaseDispatch();
        await finalCreditEntered.Task.WaitAsync(RaceCoordinationTimeout);
        await stateDispatchDrain.WaitAsync(RaceCoordinationTimeout);
        Ensure(!dispatchDrain.IsCompleted && !detached.IsCompleted && !state.IsDetached,
            "the dispatch-drained signal must not complete detach before the final credit callback reaches Detach");

        releaseFinalCredit.TrySetResult();
        await Task.WhenAll(dispatch, dispatchDrain, stateDispatchDrain, detached).WaitAsync(RaceCoordinationTimeout);
        Ensure(state.IsDetached && dispatcher.DispatchesDrainedCount == 1,
            "draining the acquired dispatch must finalize both distinct signals and return the lease once");
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

    private sealed class CapturingLeaseDispatcher : IStreamDispatcher, IStreamDispatchLease
    {
        private readonly TaskCompletionSource _dispatchesDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dispatchesDrainedCount;

        internal IStreamDispatchState DispatchState { get; private set; } = null!;

        internal int DispatchesDrainedCount => Volatile.Read(ref _dispatchesDrainedCount);

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
            => DispatchState = state;

        ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
            ReadOnlySequence<byte> payload,
            int encodedByteCount)
        {
            _ = encodedByteCount;
            return DispatchAsync(payload);
        }

        void IStreamDispatchLease.OnDispatchesDrained()
        {
            Interlocked.Increment(ref _dispatchesDrainedCount);
            _dispatchesDrained.TrySetResult();
        }
    }

    private sealed class GatedLeaseDispatcher : IStreamDispatcher, IStreamDispatchLease
    {
        private readonly TaskCompletionSource _dispatchEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _dispatchesDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dispatchesDrainedCount;

        internal IStreamDispatchState DispatchState { get; private set; } = null!;

        internal Task DispatchEntered => _dispatchEntered.Task;

        internal Task DispatchesDrained => _dispatchesDrained.Task;

        internal int DispatchesDrainedCount => Volatile.Read(ref _dispatchesDrainedCount);

        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            _dispatchEntered.TrySetResult();
            await _releaseDispatch.Task.ConfigureAwait(false);
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        internal void ReleaseDispatch() => _releaseDispatch.TrySetResult();

        void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
            => DispatchState = state;

        ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
            ReadOnlySequence<byte> payload,
            int encodedByteCount)
        {
            _ = encodedByteCount;
            return DispatchAsync(payload);
        }

        void IStreamDispatchLease.OnDispatchesDrained()
        {
            Interlocked.Increment(ref _dispatchesDrainedCount);
            _dispatchesDrained.TrySetResult();
        }
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
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nestedRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                started.TrySetResult();
                try
                {
                    manager.Register(requestId, 2, new RecordingDispatcher());
                    nestedRegistration.TrySetResult();
                }
                catch (Exception exception)
                {
                    nestedRegistration.TrySetException(exception);
                }
            })
            {
                IsBackground = true
            };
            thread.Start();
            Ensure(started.Task.Wait(RaceCoordinationTimeout),
                "nested registration worker did not start within the coordination timeout");
            RegistryLockWasHeld = !nestedRegistration.Task.Wait(RaceCoordinationTimeout);
        }
    }
}
