namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed class SendPump
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        // Protocol-progress isolation constants (issue #163): the normal class
        // cannot occupy the final ProgressReserveBytes of the queue, and the
        // pump drains the progress queue at the loop top and between every
        // NormalFramesPerInterleave normal frames. The interleave frequency
        // bounds progress service, and ProgressFramesPerDrain bounds each
        // drain so a concurrent progress producer cannot starve the normal
        // queue forever (observable under LowLatency, where every flush
        // releases capacity and the progress channel never observes empty).
        private const int NormalFramesPerInterleave = 64;
        private const int ProgressFramesPerDrain = 256;
        private const int ProgressReserveMinimumBytes = 4 * 1024;
        private const int ProgressReserveMaximumBytes = 64 * 1024;
        private const int ProgressReserveDivisor = 512;

        private enum FlushMode
        {
            LowLatency,
            Balanced,
            TimedBatch
        }

        private readonly PipeWriter _output;
        private readonly FlushMode _flushMode;
        private readonly int _flushSizeThreshold;
        private readonly TimeSpan _maxLatency;
        private readonly int _maxQueuedBytes;
        private readonly int _normalQueueLimit;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _sessionCancellation;
        private readonly Action<IRpcByteBufferWriter> _returnBuffer;
        private readonly Action<Exception> _onTransportFaulted;
        private readonly Channel<OwnedFrame> _progressQueue;
        private readonly Channel<OwnedFrame> _normalQueue;
        private readonly Lock _admissionGate = new();
        private readonly WakeupSignal _wakeup = new();
        private readonly Task _pumpTask;
        // When the caller configured an explicit MaxLatency through RpcSessionFlushOptions the
        // pump batches until that deadline even while frames keep arriving. The profile-default
        // TimedBatch deliberately skips the deadline wait instead: it flushes as soon as the
        // queue drains (like Balanced, with a larger threshold), because waiting out a batching
        // window on every drain pass interlocks the two peers' windows into a low-throughput
        // ping-pong under continuous RPC load (measured: ~1/3 of the balanced QPS at c128).
        private readonly bool _deadlineBatchingEnabled;
        private TaskCompletionSource<bool>? _capacityChanged;
        private long _queuedBytes;
        private int _stopped;
        private int _faulted;

        internal bool IsStopRequested => Volatile.Read(ref _stopped) != 0;

        public SendPump(
            PipeWriter output,
            SharpLinkPerformanceProfile performanceProfile,
            int maxQueuedBytes,
            RpcSessionFlushOptions? flushOptions,
            TimeProvider timeProvider,
            CancellationToken sessionCancellation,
            Action<IRpcByteBufferWriter> returnBuffer,
            Action<Exception> onTransportFaulted)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedBytes);
            _output = output;
            _maxQueuedBytes = maxQueuedBytes;
            _normalQueueLimit = maxQueuedBytes - ComputeProgressReserveBytes(maxQueuedBytes);
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _sessionCancellation = sessionCancellation;
            _returnBuffer = returnBuffer ?? throw new ArgumentNullException(nameof(returnBuffer));
            _onTransportFaulted = onTransportFaulted ?? throw new ArgumentNullException(nameof(onTransportFaulted));

            if (flushOptions is { } custom)
            {
                _flushMode = FlushMode.TimedBatch;
                _flushSizeThreshold = custom.FlushSizeThreshold;
                _maxLatency = custom.MaxLatency;
                _deadlineBatchingEnabled = true;
            }
            else
            {
                _deadlineBatchingEnabled = false;
                switch (performanceProfile)
                {
                    case SharpLinkPerformanceProfile.LowLatency:
                        _flushMode = FlushMode.LowLatency;
                        _flushSizeThreshold = 1;
                        _maxLatency = TimeSpan.Zero;
                        break;
                    case SharpLinkPerformanceProfile.Throughput:
                        // Throughput keeps the large coalescing threshold but flushes the
                        // moment the queue drains: frames of an active RPC pipeline leave
                        // immediately, and only a genuinely idle queue would ever want the
                        // MaxLatency deadline (which is therefore reserved for callers that
                        // configure RpcSessionFlushOptions explicitly).
                        _flushMode = FlushMode.TimedBatch;
                        _flushSizeThreshold = 64 * 1024;
                        _maxLatency = TimeSpan.FromMilliseconds(1);
                        break;
                    default:
                        _flushMode = FlushMode.Balanced;
                        _flushSizeThreshold = 16 * 1024;
                        _maxLatency = TimeSpan.Zero;
                        break;
                }
            }

            _progressQueue = CreateFrameQueue();
            _normalQueue = CreateFrameQueue();
            _pumpTask = RunAsync();
        }

        private static int ComputeProgressReserveBytes(int maxQueuedBytes)
        {
            // The headroom applies to production-sized queues. Below this
            // floor the queue is smaller than realistic frames and reserving a
            // slice would change the single-frame admission semantics that the
            // runtime's own small-queue tests rely on.
            if (maxQueuedBytes < 32 * 1024)
                return 0;
            var reserve = Math.Clamp(
                maxQueuedBytes / ProgressReserveDivisor,
                ProgressReserveMinimumBytes,
                ProgressReserveMaximumBytes);
            // Keep at least three quarters of a small queue available to the
            // normal class so a degenerate queue cannot become progress-only.
            return Math.Min(reserve, maxQueuedBytes / 4);
        }

        private bool HasProgressFrames() => _progressQueue.Reader.TryPeek(out _);

        private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _);

        private static Channel<OwnedFrame> CreateFrameQueue()
            => Channel.CreateUnbounded<OwnedFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        public SendEnqueueResult TryEnqueue(OwnedFrame frame)
            => TryEnqueue(frame, returnFrameWhenFull: true);

        public SendEnqueueResult TryEnqueueForBackpressure(OwnedFrame frame)
            => TryEnqueue(frame, returnFrameWhenFull: false);

        private SendEnqueueResult TryEnqueue(OwnedFrame frame, bool returnFrameWhenFull)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                ReturnUnreserved(frame, CreateTransportClosedException());
                return SendEnqueueResult.Closed;
            }
            if (!TryReserve(frame.Length, frame.IsProtocolProgress))
            {
                if (returnFrameWhenFull)
                {
                    ReturnUnreserved(frame, SharpLinkResourceExhaustion.Create(
                        SharpLinkResourceExhaustion.SendQueueCapacity,
                        $"Session send queue exceeded its {_maxQueuedBytes}-byte limit (send_queue_capacity)."));
                }
                return SendEnqueueResult.Full;
            }
            var queue = frame.IsProtocolProgress ? _progressQueue : _normalQueue;
            if (queue.Writer.TryWrite(frame))
            {
                _wakeup.Signal();
                return SendEnqueueResult.Accepted;
            }

            CompleteReserved(frame, CreateTransportClosedException());
            return SendEnqueueResult.Closed;
        }

        public async ValueTask<SendEnqueueResult> EnqueueAsync(
            OwnedFrame frame,
            CancellationToken cancellationToken)
        {
            try
            {
                await ReserveAsync(frame.Length, frame.IsProtocolProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                ReturnUnreserved(frame);
                throw;
            }

            if (Volatile.Read(ref _stopped) == 0 &&
                (frame.IsProtocolProgress ? _progressQueue : _normalQueue).Writer.TryWrite(frame))
            {
                _wakeup.Signal();
                return SendEnqueueResult.Accepted;
            }

            CompleteReserved(frame, exception: null, completeFlushWaiter: false);
            return SendEnqueueResult.Closed;
        }

        private async Task RunAsync()
        {
            var pending = new List<OwnedFrame>(32);
            Exception terminalException = CreateTransportClosedException();
            var bytesAccumulated = 0;
            var batchDeadline = 0L;
            var writtenCount = 0;
            var deferWrites = false;

            try
            {
                while (true)
                {
                    if (!HasProgressFrames() && !HasNormalFrames())
                    {
                        if (Volatile.Read(ref _stopped) != 0)
                            break;

                        // Arm the reusable wakeup signal and always await it. WaitAsync
                        // consumes any signal that arrived before the arm was published,
                        // so a frame written between the empty-queue check above and the
                        // arm cannot leave the await hanging, and the arm never has to be
                        // abandoned. Idle wakeups allocate nothing.
                        var wakeup = _wakeup.WaitAsync();
                        await wakeup.ConfigureAwait(false);
                        continue;
                    }

                    if (await DrainProgressQueueAsync(pending, deferWrites).ConfigureAwait(false))
                    {
                        // Progress frames must not wait for a full batch:
                        // flush whatever the batch still holds (LowLatency
                        // already flushed per frame inside the drain).
                        if (!deferWrites)
                            writtenCount = pending.Count;
                        if (pending.Count > 0)
                        {
                            await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            writtenCount = 0;
                            deferWrites = false;
                        }
                        batchDeadline = 0;
                    }

                    var normalFramesSinceInterleave = 0;
                    while (_normalQueue.Reader.TryRead(out var frame))
                    {
                        if (pending.Count == 0)
                        {
                            batchDeadline = SharpLinkTime.AddDuration(
                                _timeProvider.GetTimestamp(),
                                _maxLatency,
                                _timeProvider.TimestampFrequency);
                        }

                        // Take ownership of the frame before any write can fail: a fault during
                        // WriteFrame/FlushAsync must still release the frame and complete its
                        // flush waiter through the terminal ReleaseBatch in the finally block.
                        pending.Add(frame);
                        var hasTimeBudget = HasTimeBudget(frame);
                        if (!deferWrites)
                        {
                            if (hasTimeBudget)
                                deferWrites = true;
                            else
                            {
                                WriteFrame(frame);
                                writtenCount++;
                            }
                        }
                        bytesAccumulated += frame.Length;

                        // A deadline-bearing Request is a publication boundary. Its retained
                        // process-local deadline is sampled only after output span/copy has
                        // completed, and no later frame may perform local work before the flush
                        // that publishes that budget snapshot.
                        if (hasTimeBudget ||
                            frame.ForceFlush ||
                            _flushMode == FlushMode.LowLatency ||
                            bytesAccumulated >= _flushSizeThreshold)
                        {
                            await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchDeadline = 0;
                            writtenCount = 0;
                            deferWrites = false;
                        }

                        // Bounded progress interleave: the progress check is
                        // independent of flush boundaries, otherwise frames at
                        // or above the flush threshold would flush every time
                        // and the interleave would never fire, starving the
                        // progress queue while the normal queue never empties.
                        normalFramesSinceInterleave++;
                        if (normalFramesSinceInterleave >= NormalFramesPerInterleave)
                        {
                            normalFramesSinceInterleave = 0;
                            if (await DrainProgressQueueAsync(pending, deferWrites).ConfigureAwait(false))
                            {
                                if (!deferWrites)
                                    writtenCount = pending.Count;
                                if (pending.Count > 0)
                                {
                                    await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                                    bytesAccumulated = 0;
                                    writtenCount = 0;
                                    deferWrites = false;
                                }
                                batchDeadline = 0;
                            }
                        }
                    }

                    if (pending.Count == 0)
                        continue;

                    // Profile-default TimedBatch treats the queue drain as the flush point
                    // (see _deadlineBatchingEnabled): only an explicitly configured
                    // MaxLatency enters the deadline wait, keeping the public
                    // RpcSessionFlushOptions contract for latency-bounded batching.
                    if (_flushMode == FlushMode.TimedBatch &&
                        _deadlineBatchingEnabled &&
                        await WaitForMoreUntilDeadlineAsync(batchDeadline).ConfigureAwait(false) &&
                        (HasProgressFrames() || HasNormalFrames()))
                    {
                        continue;
                    }

                    await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchDeadline = 0;
                    writtenCount = 0;
                    deferWrites = false;
                }
            }
            catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                terminalException = NormalizeTransportException(ex);
                ReportFaultOnce(terminalException);
            }
            finally
            {
                ReleaseBatch(pending, terminalException);
                DrainQueuedFrames(terminalException);
                PulseCapacityWaiters();
            }
        }

        private async ValueTask<bool> DrainProgressQueueAsync(
            List<OwnedFrame> pending,
            bool deferWrites)
        {
            // The drain runs until the progress queue is empty so the service
            // rate always matches the arrival rate. If an earlier deadline-bearing
            // frame is deferred, progress stays behind it; otherwise preserve the
            // original immediate-copy ordering and only delay the transport flush.
            var drained = false;
            var drainedCount = 0;
            while (drainedCount < ProgressFramesPerDrain &&
                   _progressQueue.Reader.TryRead(out var frame))
            {
                pending.Add(frame);
                if (!deferWrites)
                    WriteFrame(frame);
                drained = true;
                drainedCount++;
                if (_flushMode == FlushMode.LowLatency)
                {
                    await FlushAndReleaseAsync(
                        pending,
                        deferWrites ? 0 : pending.Count).ConfigureAwait(false);
                }
            }
            return drained;
        }

        private static bool HasTimeBudget(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            return source.Length >=
                       ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes + sizeof(long) &&
                   (ProtocolV2FrameType)source[5] == ProtocolV2FrameType.Request &&
                   (((ProtocolV2FrameFlags)source[6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0;
        }

        private void WriteFrame(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return;
            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            _output.Advance(source.Length);
        }

        private bool TryWriteFrameAtEmission(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return true;
            if (!HasTimeBudget(frame))
            {
                WriteFrame(frame);
                return true;
            }
            if (!frame.Deadline.HasValue)
            {
                throw new InvalidOperationException(
                    "A Request carrying TimeBudget must retain its process-local RpcDeadline until emission.");
            }

            var budgetOffset = ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes;

            // GetSpan/copy are still local pre-publication work and may be supplied by a
            // custom PipeWriter. Finish that work before sampling the remaining budget so
            // it cannot silently extend the peer's lifetime.
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            var remaining = frame.Deadline.GetRemaining(_timeProvider);
            if (remaining <= TimeSpan.Zero)
                return false;

            SharpLinkTelemetry.RecordSentBytes(source.Length);
            BinaryPrimitives.WriteInt64LittleEndian(
                destination.Slice(budgetOffset, sizeof(long)),
                remaining.Ticks);
            _output.Advance(source.Length);
            return true;
        }

        private async ValueTask FlushAndReleaseAsync(
            List<OwnedFrame> pending,
            int writtenCount)
        {
            // Only the suffix beginning with the first deadline-bearing request stays in
            // owned buffers. Stamp its remaining TimeBudget from retained deadline metadata
            // at the last possible point before FlushAsync.
            for (var index = writtenCount; index < pending.Count;)
            {
                var frame = pending[index];
                if (TryWriteFrameAtEmission(frame))
                {
                    index++;
                    continue;
                }

                pending.RemoveAt(index);
                CompleteReserved(
                    frame,
                    new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        "Request deadline expired before transport emission."),
                    completeFlushWaiter: true);
            }

            if (pending.Count == 0)
                return;

            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }

        private async ValueTask<bool> WaitForMoreUntilDeadlineAsync(long batchDeadline)
        {
            // The queue and the explicit batching deadline now share one readiness authority:
            // producers signal WakeupSignal, and the deadline timer competes for that same arm.
            // No Channel read is registered here, so there is nothing to retain, abandon, or
            // observe during teardown.
            while (true)
            {
                if (HasProgressFrames() || HasNormalFrames())
                    return true;

                // Signals coalesce while the pump is busy, so the frames just drained can leave
                // a latch behind. That latch is already accounted for and must not terminate the
                // explicit MaxLatency window. Consume it, then re-check both queues before
                // arming: a producer whose signal crosses this CAS has already published its
                // frame, so the re-check preserves the no-lost-wakeup guarantee.
                _wakeup.ConsumeLatched();
                if (HasProgressFrames() || HasNormalFrames())
                    return true;

                var remaining = SharpLinkTime.GetRemaining(
                    batchDeadline,
                    _timeProvider.GetTimestamp(),
                    _timeProvider.TimestampFrequency);
                if (remaining == TimeSpan.Zero)
                    return false;

                var delay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                if (await _wakeup.WaitAsync(_timeProvider, delay).ConfigureAwait(false))
                    return true;

                if (remaining <= MaximumTimerDelay)
                    return false;
                // A chunk of a very long deadline expired. Re-evaluate queue visibility and
                // remaining time before arming the next generation of the same wake authority.
            }
        }

        private bool TryReserve(int bytes, bool isProtocolProgress)
        {
            if (bytes < 0)
                return false;
            if (bytes == 0)
                return true;

            // Protocol-progress frames may use the full queue budget; normal
            // frames may not occupy the reserved progress headroom. A normal
            // frame larger than its limit is rejected, even on an empty queue,
            // so it cannot consume the reserve and break liveness isolation
            // under transport saturation. When the queue is too small to hold
            // any reserve the headroom does not exist and the base single-frame
            // oversized exception is preserved; progress frames keep the base
            // oversized semantics (admitted once when the queue is empty).
            var limit = isProtocolProgress ? _maxQueuedBytes : _normalQueueLimit;

            while (true)
            {
                var current = Volatile.Read(ref _queuedBytes);
                var canReserve = bytes <= limit
                    ? current <= limit - bytes
                    : current == 0 && (isProtocolProgress || _normalQueueLimit == _maxQueuedBytes);
                if (!canReserve)
                    return false;
                if (Interlocked.CompareExchange(ref _queuedBytes, current + bytes, current) == current)
                {
                    SharpLinkTelemetry.AddSendQueueBytes(bytes);
                    return true;
                }
            }
        }

        private async ValueTask ReserveAsync(
            int bytes,
            bool isProtocolProgress,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopped) != 0)
                    throw CreateTransportClosedException();
                if (TryReserve(bytes, isProtocolProgress))
                    return;

                Task waitTask;
                lock (_admissionGate)
                {
                    if (Volatile.Read(ref _stopped) != 0)
                        throw CreateTransportClosedException();
                    if (TryReserve(bytes, isProtocolProgress))
                        return;
                    _capacityChanged ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    waitTask = _capacityChanged.Task;
                }
                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ReleaseBatch(List<OwnedFrame> pending, Exception? exception)
        {
            for (var index = 0; index < pending.Count; index++)
                CompleteReserved(pending[index], exception, completeFlushWaiter: true);
            pending.Clear();
        }

        private void DrainQueuedFrames(Exception exception)
        {
            while (_progressQueue.Reader.TryRead(out var frame))
                CompleteReserved(frame, exception, completeFlushWaiter: true);
            while (_normalQueue.Reader.TryRead(out var frame))
                CompleteReserved(frame, exception, completeFlushWaiter: true);
        }

        private void CompleteReserved(
            OwnedFrame frame,
            Exception? exception,
            bool completeFlushWaiter = true)
        {
            try
            {
                _returnBuffer(frame.Owner);
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -frame.Length);
                SharpLinkTelemetry.AddSendQueueBytes(-frame.Length);
                if (completeFlushWaiter)
                {
                    if (exception is null)
                        frame.FlushCompletion?.TrySetResult(true);
                    else
                        frame.FlushCompletion?.TrySetException(exception);
                }
                PulseCapacityWaiters();
            }
        }

        private void ReturnUnreserved(OwnedFrame frame, Exception? exception = null)
        {
            _returnBuffer(frame.Owner);
        }

        public long QueuedBytes => Volatile.Read(ref _queuedBytes);

        private void PulseCapacityWaiters()
        {
            if (Volatile.Read(ref _capacityChanged) is null)
                return;

            TaskCompletionSource<bool>? waiters;
            lock (_admissionGate)
            {
                waiters = _capacityChanged;
                _capacityChanged = null;
            }
            waiters?.TrySetResult(true);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _progressQueue.Writer.TryComplete();
            _normalQueue.Writer.TryComplete();
            _wakeup.Signal();
            PulseCapacityWaiters();
        }

        private void ReportFaultOnce(Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) != 0)
                return;
            Interlocked.Exchange(ref _stopped, 1);
            _progressQueue.Writer.TryComplete(exception);
            _normalQueue.Writer.TryComplete(exception);
            _wakeup.Signal();
            PulseCapacityWaiters();
            _onTransportFaulted(exception);
        }

        private static SharpLinkException NormalizeTransportException(Exception exception)
            => exception as SharpLinkException ??
               new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport output failed.", exception);

        private static SharpLinkException CreateTransportClosedException()
            => new(SharpLinkErrorCode.ConnectionClosed, "Transport output completed.");

        public ValueTask WaitForStopAsync()
            => _pumpTask.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(_pumpTask);
    }
}
