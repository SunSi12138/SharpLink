namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed class SendPump
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        // Protocol-progress isolation constants (issue #163): the normal class
        // cannot occupy the final ProgressReserveBytes of the queue, and the
        // drain interleaves at most ProgressBurstFrames progress frames between
        // NormalFramesPerInterleave normal frames so neither class can starve
        // the other.
        private const int ProgressBurstFrames = 8;
        private const int NormalFramesPerInterleave = 64;
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
        private readonly DeadlineReadRace _deadlineRace;
        private readonly Task _pumpTask;
        private TaskCompletionSource<bool>? _capacityChanged;
        private Task<bool>? _pendingReadWait;
        private Task<bool>? _pendingProgressReadWait;
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
            }
            else
            {
                switch (performanceProfile)
                {
                    case SharpLinkPerformanceProfile.LowLatency:
                        _flushMode = FlushMode.LowLatency;
                        _flushSizeThreshold = 1;
                        _maxLatency = TimeSpan.Zero;
                        break;
                    case SharpLinkPerformanceProfile.Throughput:
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
            _deadlineRace = new DeadlineReadRace(_timeProvider);
            _pumpTask = RunAsync();
        }

        private static int ComputeProgressReserveBytes(int maxQueuedBytes)
        {
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
                return SendEnqueueResult.Accepted;

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

            try
            {
                while (await WaitForFramesAsync().ConfigureAwait(false))
                {
                    if (DrainProgressBurst(pending, ref bytesAccumulated))
                    {
                        // Progress frames must not wait for a full batch:
                        // flush whatever the batch holds right now.
                        await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                        bytesAccumulated = 0;
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
                        WriteFrame(frame);
                        bytesAccumulated += frame.Length;
                        normalFramesSinceInterleave++;

                        if (frame.ForceFlush ||
                            _flushMode == FlushMode.LowLatency ||
                            bytesAccumulated >= _flushSizeThreshold)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchDeadline = 0;
                            normalFramesSinceInterleave = 0;
                        }
                        else if (normalFramesSinceInterleave >= NormalFramesPerInterleave)
                        {
                            // Bounded progress interleave: check the progress
                            // queue even while the normal queue never empties.
                            normalFramesSinceInterleave = 0;
                            if (DrainProgressBurst(pending, ref bytesAccumulated))
                            {
                                await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                                bytesAccumulated = 0;
                                batchDeadline = 0;
                            }
                        }
                    }

                    if (pending.Count == 0)
                        continue;

                    if (_flushMode == FlushMode.TimedBatch &&
                        await WaitForMoreUntilDeadlineAsync(batchDeadline).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchDeadline = 0;
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
                _deadlineRace.Dispose();
                ReleaseBatch(pending, terminalException);
                DrainQueuedFrames(terminalException);
                PulseCapacityWaiters();
            }
        }

        private bool DrainProgressBurst(List<OwnedFrame> pending, ref int bytesAccumulated)
        {
            var drained = false;
            for (var count = 0;
                 count < ProgressBurstFrames && _progressQueue.Reader.TryRead(out var frame);
                 count++)
            {
                pending.Add(frame);
                WriteFrame(frame);
                bytesAccumulated += frame.Length;
                drained = true;
                if (frame.ForceFlush ||
                    _flushMode == FlushMode.LowLatency ||
                    bytesAccumulated >= _flushSizeThreshold)
                {
                    break;
                }
            }
            return drained;
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

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }

        /// <summary>
        /// Waits until either queue has data. The fast path peeks both queues
        /// without allocating; the idle path awaits the first completed read.
        /// A read that loses the race is intentionally abandoned: its data
        /// stays queued and is observed by the next fast path.
        /// </summary>
        private ValueTask<bool> WaitForFramesAsync()
        {
            if (HasProgressFrames() || HasNormalFrames())
                return ValueTask.FromResult(true);

            var progressWait = _progressQueue.Reader.WaitToReadAsync(CancellationToken.None);
            if (progressWait.IsCompletedSuccessfully)
                return progressWait;
            var progressTask = progressWait.AsTask();

            var normalWait = WaitToReadAsync();
            if (normalWait.IsCompletedSuccessfully)
                return normalWait;
            var normalTask = normalWait.AsTask();

            return new ValueTask<bool>(AwaitFirstReadAsync(progressTask, normalTask));
        }

        private static async Task<bool> AwaitFirstReadAsync(Task<bool> first, Task<bool> second)
        {
            var winner = await Task.WhenAny(first, second).ConfigureAwait(false);
            return await winner.ConfigureAwait(false);
        }

        private async ValueTask<bool> WaitForMoreUntilDeadlineAsync(long batchDeadline)
        {
            // Reuse a retained normal read when one is still registered on the
            // channel: the TryPeek fast path in WaitForFramesAsync can leave a
            // retained read behind, and re-creating reads every deadline cycle
            // would abandon one registered read per cycle.
            Task<bool> pendingRead;
            if (_pendingReadWait is { } retained)
            {
                pendingRead = retained;
                if (pendingRead.IsCompletedSuccessfully)
                    return pendingRead.Result;
            }
            else
            {
                var waitToRead = _normalQueue.Reader.WaitToReadAsync(CancellationToken.None);
                if (waitToRead.IsCompletedSuccessfully)
                    return waitToRead.Result;
                pendingRead = waitToRead.AsTask();
                _pendingReadWait = pendingRead;
            }
            // The progress read ends the batching deadline immediately so protocol
            // progress is not delayed by the batch window; it is retained across
            // timer chunks like the normal read.
            _pendingProgressReadWait ??=
                _progressQueue.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
            var progressRead = _pendingProgressReadWait;
            while (true)
            {
                var remaining = SharpLinkTime.GetRemaining(
                    batchDeadline,
                    _timeProvider.GetTimestamp(),
                    _timeProvider.TimestampFrequency);
                if (remaining == TimeSpan.Zero)
                    return false;

                var delay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                if (await _deadlineRace
                        .WaitForReadsOrTimeout(pendingRead, progressRead, delay)
                        .ConfigureAwait(false))
                {
                    if (_deadlineRace.Outcome == DeadlineReadRace.RaceOutcome.ProgressAvailable)
                        _pendingProgressReadWait = null;
                    else
                        _pendingReadWait = null;
                    return true;
                }

                switch (_deadlineRace.Outcome)
                {
                    case DeadlineReadRace.RaceOutcome.ReadClosed:
                        _pendingReadWait = null;
                        _pendingProgressReadWait = null;
                        return false;
                    case DeadlineReadRace.RaceOutcome.TimedOut when remaining > MaximumTimerDelay:
                        // A chunk of a very long deadline expired: re-arm the same retained reads.
                        continue;
                    default:
                        // The deadline expired and the pending reads were not consumed: they stay
                        // retained in _pendingReadWait/_pendingProgressReadWait for re-observation.
                        return false;
                }
            }
        }

        private ValueTask<bool> WaitToReadAsync()
        {
            var pendingRead = _pendingReadWait;
            if (pendingRead is null)
                return _normalQueue.Reader.WaitToReadAsync(CancellationToken.None);

            _pendingReadWait = null;
            return new ValueTask<bool>(pendingRead);
        }

        private bool TryReserve(int bytes, bool isProtocolProgress)
        {
            if (bytes < 0)
                return false;
            if (bytes == 0)
                return true;

            // Protocol-progress frames may use the full queue budget; normal
            // frames may not occupy the reserved progress headroom.
            var limit = isProtocolProgress ? _maxQueuedBytes : _normalQueueLimit;

            while (true)
            {
                var current = Volatile.Read(ref _queuedBytes);
                var canReserve = bytes <= limit
                    ? current <= limit - bytes
                    : current == 0;
                if (!canReserve)
                {
                    if (Environment.GetEnvironmentVariable("SHARPLINK_DEBUG_RESERVE") == "1")
                        Console.WriteLine($"[ReserveFull] bytes={bytes} progress={isProtocolProgress} limit={limit} current={current}");
                    return false;
                }
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
            PulseCapacityWaiters();
        }

        private void ReportFaultOnce(Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) != 0)
                return;
            Interlocked.Exchange(ref _stopped, 1);
            _progressQueue.Writer.TryComplete(exception);
            _normalQueue.Writer.TryComplete(exception);
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
