namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed partial class SendPump
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        // Protocol-progress headroom and bounded interleaving remain unchanged.
        private const int NormalFramesPerInterleave = 64;
        private const int ProgressFramesPerDrain = 256;
        private const int ProgressReserveMinimumBytes = 4 * 1024;
        private const int ProgressReserveMaximumBytes = 64 * 1024;
        private const int ProgressReserveDivisor = 512;

        private readonly PipeWriter _output;
        private readonly RpcSessionFlushPolicyState _flushPolicyState;
        private readonly Action _flushPolicyChanged;
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
        private TaskCompletionSource<bool>? _capacityChanged;
        private long _queuedBytes;
        private int _stopped;
        private int _faulted;

        internal bool IsStopRequested => Volatile.Read(ref _stopped) != 0;
        internal bool HasPendingIdleWait => _wakeup.HasPendingIdleWait;

        public SendPump(
            PipeWriter output,
            RpcSessionFlushPolicyState flushPolicyState,
            int maxQueuedBytes,
            TimeProvider timeProvider,
            CancellationToken sessionCancellation,
            Action<IRpcByteBufferWriter> returnBuffer,
            Action<Exception> onTransportFaulted)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(flushPolicyState);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedBytes);
            _output = output;
            _flushPolicyState = flushPolicyState;
            _maxQueuedBytes = maxQueuedBytes;
            _normalQueueLimit = maxQueuedBytes - ComputeProgressReserveBytes(maxQueuedBytes);
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _sessionCancellation = sessionCancellation;
            _returnBuffer = returnBuffer ?? throw new ArgumentNullException(nameof(returnBuffer));
            _onTransportFaulted = onTransportFaulted ?? throw new ArgumentNullException(nameof(onTransportFaulted));
            _flushPolicyChanged = _wakeup.Signal;
            _flushPolicyState.RegisterChanged(_flushPolicyChanged);
            _progressQueue = CreateFrameQueue();
            _normalQueue = CreateFrameQueue();
            _pumpTask = RunAsync();
        }

        private static int ComputeProgressReserveBytes(int maxQueuedBytes)
        {
            if (maxQueuedBytes < 32 * 1024)
                return 0;
            var reserve = Math.Clamp(
                maxQueuedBytes / ProgressReserveDivisor,
                ProgressReserveMinimumBytes,
                ProgressReserveMaximumBytes);
            return Math.Min(reserve, maxQueuedBytes / 4);
        }

        private bool HasProgressFrames() => _progressQueue.Reader.TryPeek(out _);

        private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _) || HasWriterReadyWork();

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
            var batchStartTimestamp = 0L;
            try
            {
                while (true)
                {
                    if (!HasProgressFrames() && !HasNormalFrames())
                    {
                        if (Volatile.Read(ref _stopped) != 0)
                            break;
                        // Always consume the arm; WakeupSignal covers publication crossings.
                        var wakeup = _wakeup.WaitAsync();
                        await wakeup.ConfigureAwait(false);
                        continue;
                    }
                    if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))
                    {
                        if (pending.Count > 0)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                        }
                        batchStartTimestamp = 0;
                    }
                    var normalFramesSinceInterleave = 0;
                    while (TryReadOrdinaryOrWriterReadyFrame(out var frame))
                    {
                        if (pending.Count == 0)
                            batchStartTimestamp = _timeProvider.GetTimestamp();
                        // Take ownership before any copy/flush that can fail.
                        pending.Add(frame);
                        bytesAccumulated += frame.Length;
                        var flushPolicy = _flushPolicyState.Capture();
                        if (frame.ForceFlush ||
                            flushPolicy.FlushEveryFrame ||
                            bytesAccumulated >= flushPolicy.FlushSizeThreshold)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchStartTimestamp = 0;
                        }
                        normalFramesSinceInterleave++;
                        if (normalFramesSinceInterleave >= NormalFramesPerInterleave)
                        {
                            normalFramesSinceInterleave = 0;
                            if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))
                            {
                                if (pending.Count > 0)
                                {
                                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                                    bytesAccumulated = 0;
                                }
                                batchStartTimestamp = 0;
                            }
                        }
                    }
                    if (pending.Count == 0)
                        continue;
                    // Preserve the explicit user batching policy on attached connections.
                    if (_flushPolicyState.Capture().ExplicitBatchWindowEnabled &&
                        await WaitForMoreUntilFlushBoundaryAsync(
                            batchStartTimestamp,
                            bytesAccumulated).ConfigureAwait(false) &&
                        (HasProgressFrames() || HasNormalFrames()))
                    {
                        continue;
                    }
                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchStartTimestamp = 0;
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
                CleanupAfterStop(pending, terminalException);
            }
        }

        private async ValueTask<bool> DrainProgressQueueAsync(List<OwnedFrame> pending)
        {
            var drained = false;
            var drainedCount = 0;
            while (drainedCount < ProgressFramesPerDrain &&
                   _progressQueue.Reader.TryRead(out var frame))
            {
                pending.Add(frame);
                drained = true;
                drainedCount++;
                if (_flushPolicyState.Capture().FlushEveryFrame)
                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
            }
            return drained;
        }

        private void WritePendingBatch(List<OwnedFrame> pending)
        {
            var length = 0;
            for (var index = 0; index < pending.Count; index++)
                length = checked(length + pending[index].Length);
            if (length == 0)
                return;
            var destination = _output.GetSpan(length);
            var offset = 0;
            for (var index = 0; index < pending.Count; index++)
            {
                var frame = pending[index];
                if (frame.Length == 0)
                    continue;
                frame.Memory.Span.CopyTo(destination[offset..]);
                offset += frame.Length;
            }
            _output.Advance(offset);
            SharpLinkTelemetry.RecordSentBytes(offset);
        }

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            if (pending.Count == 0)
                return;
            WritePendingBatch(pending);
            var flush = _output.FlushAsync(_sessionCancellation);
            var result = await flush.ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }

        private async ValueTask<bool> WaitForMoreUntilFlushBoundaryAsync(
            long batchStartTimestamp,
            int bytesAccumulated)
        {
            while (true)
            {
                if (HasProgressFrames() || HasNormalFrames())
                    return true;
                var policy = _flushPolicyState.Capture();
                if (policy.FlushEveryFrame || bytesAccumulated >= policy.FlushSizeThreshold)
                    return false;
                if (!policy.ExplicitBatchWindowEnabled)
                    return false;
                var deadline = SharpLinkTime.AddDuration(
                    batchStartTimestamp, policy.MaxLatency, _timeProvider.TimestampFrequency);
                var remaining = SharpLinkTime.GetRemaining(
                    deadline, _timeProvider.GetTimestamp(), _timeProvider.TimestampFrequency);
                if (remaining == TimeSpan.Zero)
                    return false;
                _wakeup.ConsumeLatched();
                if (Volatile.Read(ref _stopped) != 0)
                    return false;
                if (HasProgressFrames() || HasNormalFrames())
                    return true;
                if (!ReferenceEquals(policy, _flushPolicyState.Capture()))
                    continue;
                var delay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                var woke = await _wakeup.WaitAsync(_timeProvider, delay).ConfigureAwait(false);
                if (!ReferenceEquals(policy, _flushPolicyState.Capture()))
                    continue;
                if (woke)
                    continue;
                if (remaining <= MaximumTimerDelay)
                    return false;
            }
        }

        private bool TryReserve(int bytes, bool isProtocolProgress)
        {
            if (bytes < 0)
                return false;
            if (bytes == 0)
                return true;
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
