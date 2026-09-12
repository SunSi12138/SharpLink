using System.Buffers;

namespace SharpLink.UnitTests.Runtime;

public class PreAdmissionStreamDispatcherTests
{
    [Test]
    public async Task DeferredBufferShouldStopAt4096TinyItems()
    {
        const long requestId = 73;
        const ushort streamId = 1;
        var releasedBytes = 0;
        var capacityExceeded = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            bytes => releasedBytes += bytes,
            () => capacityExceeded++);

        var tinyPayload = new ReadOnlySequence<byte>(new byte[] { 1 });
        for (var index = 0; index <= 4096; index++)
            await manager.DispatchChunkAsync(requestId, streamId, tinyPayload);

        await Assert.That(capacityExceeded).IsEqualTo(1);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(4096);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(
            dispatcher.LastException is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }).IsTrue();
        await Assert.That(releasedBytes).IsEqualTo(4097);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    [Test]
    public async Task RepeatedReservationShouldSwitchFutureRetentionWithoutMigratingBufferedOwnership()
    {
        const long requestId = 91;
        const ushort streamId = 1;
        var queuedBytes = 0;
        var queueCapacityExceeded = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            bytes =>
            {
                if (queuedBytes + bytes > 2)
                    return false;
                queuedBytes += bytes;
                return true;
            },
            bytes => queuedBytes -= bytes,
            () => queueCapacityExceeded++);

        var tinyPayload = new ReadOnlySequence<byte>(new byte[] { 1 });
        await manager.DispatchChunkAsync(requestId, streamId, tinyPayload);
        await manager.DispatchChunkAsync(requestId, streamId, tinyPayload);
        await Assert.That(queuedBytes).IsEqualTo(2);

        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { });

        // Reconfiguration changes only future admission. Existing owners keep the release callback
        // that admitted them instead of migrating permits to a replacement dispatcher/policy.
        await Assert.That(queuedBytes).IsEqualTo(2);
        for (var index = 0; index < 8; index++)
            await manager.DispatchChunkAsync(requestId, streamId, tinyPayload);
        await Assert.That(queuedBytes).IsEqualTo(2);
        await Assert.That(queueCapacityExceeded).IsEqualTo(0);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(10);
        await Assert.That(queuedBytes).IsEqualTo(0);
        manager.CompleteStream(requestId, streamId, exception: null);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    [Test]
    public async Task AttachReplayAndLiveIngressShouldShare4096ElementLimit()
    {
        const long requestId = 117;
        const ushort streamId = 1;
        var capacityExceeded = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            () => capacityExceeded++);

        var tinyPayload = new ReadOnlySequence<byte>(new byte[] { 1 });
        for (var index = 0; index < 4095; index++)
            await manager.DispatchChunkAsync(requestId, streamId, tinyPayload);

        var dispatcher = new GatedRecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);
        await dispatcher.FirstDispatchStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // One replayed item plus 4094 still queued items leaves exactly one handoff slot.
        // The first live frame may use it without blocking the transport reader; the next frame
        // must hit the same 4096-element budget instead of refilling an independent outer queue.
        var acceptedLive = manager.DispatchChunkAsync(requestId, streamId, tinyPayload);
        await Assert.That(acceptedLive.IsCompletedSuccessfully).IsTrue();
        var overflowLive = manager.DispatchChunkAsync(requestId, streamId, tinyPayload);
        await Assert.That(overflowLive.IsCompletedSuccessfully).IsTrue();
        await Assert.That(capacityExceeded).IsEqualTo(1);

        dispatcher.ReleaseFirst();
        await dispatcher.Completed.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(4096);
        await Assert.That(
            dispatcher.LastException is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }).IsTrue();

        manager.CompleteStream(requestId, streamId, exception: null);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    [Test]
    public async Task TypedChildShouldBindDirectlyToStableMailboxState()
    {
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers,
            static _ => true,
            static _ => { },
            static () => { });
        var dispatcher = new LeaseCapturingDispatcher();

        await Assert.That(mailbox.TryBeginAttach(dispatcher, out var alreadyCompleted)).IsTrue();
        mailbox.FinishAttach(dispatcher);

        await Assert.That(alreadyCompleted).IsFalse();
        await Assert.That(ReferenceEquals(dispatcher.DispatchState, mailbox)).IsTrue();

        var state = (IStreamDispatchState)mailbox;
        state.Close();
        await Assert.That(state.HasActiveDispatches).IsFalse();
        await Assert.That(state.IsDetached).IsFalse();
        await state.WaitForDispatchesDrainedAsync();

        var detached = state.WaitForDetachedAsync(CancellationToken.None);
        await Assert.That(detached.IsCompletedSuccessfully).IsFalse();

        mailbox.Abandon(out _);
        await detached;
        await Assert.That(state.IsDetached).IsTrue();
    }

    private class RecordingDispatcher : IStreamDispatcher
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DispatchCount { get; protected set; }
        public int CompleteCount { get; private set; }
        public Exception? LastException { get; private set; }
        public Task Completed => _completed.Task;

        public virtual ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            DispatchCount++;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            CompleteCount++;
            LastException = isError
                ? new SharpLinkException(
                    SharpLinkErrorCode.RemoteError,
                    errorMessage ?? "Remote Error")
                : null;
            _completed.TrySetResult();
        }

        public void Complete(Exception? exception)
        {
            CompleteCount++;
            LastException = exception;
            _completed.TrySetResult();
        }
    }

    private sealed class GatedRecordingDispatcher : RecordingDispatcher
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstDispatchStarted => _firstStarted.Task;

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public override ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            DispatchCount++;
            if (DispatchCount != 1)
                return ValueTask.CompletedTask;

            _firstStarted.TrySetResult();
            return new ValueTask(_releaseFirst.Task);
        }
    }

    private sealed class LeaseCapturingDispatcher : RecordingDispatcher, IStreamDispatchLease
    {
        internal IStreamDispatchState? DispatchState { get; private set; }

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
        }
    }
}
