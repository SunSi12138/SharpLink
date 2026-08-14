namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed class SendPump
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);
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
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _sessionCancellation;
        private readonly Action<IRpcByteBufferWriter> _returnBuffer;
        private readonly Action<Exception> _onTransportFaulted;
        private readonly Channel<OwnedFrame> _queue;
        private readonly Lock _admissionGate = new();
        private readonly Task _pumpTask;
        private TaskCompletionSource<bool>? _capacityChanged;
        private Task<bool>? _pendingReadWait;
        private CancellationTokenSource? _delayCancellation;
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

            _queue = Channel.CreateUnbounded<OwnedFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _pumpTask = RunAsync();
        }

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
            if (!TryReserve(frame.Length))
            {
                if (returnFrameWhenFull)
                {
                    ReturnUnreserved(frame, SharpLinkResourceExhaustion.Create(
                        SharpLinkResourceExhaustion.SendQueueCapacity,
                        $"Session send queue exceeded its {_maxQueuedBytes}-byte limit (send_queue_capacity)."));
                }
                return SendEnqueueResult.Full;
            }
            if (_queue.Writer.TryWrite(frame))
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
                await ReserveAsync(frame.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                ReturnUnreserved(frame);
                throw;
            }

            if (Volatile.Read(ref _stopped) == 0 && _queue.Writer.TryWrite(frame))
                return SendEnqueueResult.Accepted;

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
                while (await WaitToReadAsync().ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var frame))
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

                        if (frame.ForceFlush ||
                            _flushMode == FlushMode.LowLatency ||
                            bytesAccumulated >= _flushSizeThreshold)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchDeadline = 0;
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
                _delayCancellation?.Dispose();
                ReleaseBatch(pending, terminalException);
                DrainQueuedFrames(terminalException);
                PulseCapacityWaiters();
            }
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

        private async ValueTask<bool> WaitForMoreUntilDeadlineAsync(long batchDeadline)
        {
            var waitToRead = _queue.Reader.WaitToReadAsync(_sessionCancellation);
            if (waitToRead.IsCompletedSuccessfully)
                return waitToRead.Result;

            var pendingRead = waitToRead.AsTask();
            _pendingReadWait = pendingRead;
            while (true)
            {
                var remaining = SharpLinkTime.GetRemaining(
                    batchDeadline,
                    _timeProvider.GetTimestamp(),
                    _timeProvider.TimestampFrequency);
                if (remaining == TimeSpan.Zero)
                    return false;

                var delay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                var delayCancellation = _delayCancellation;
                if (delayCancellation is null || delayCancellation.IsCancellationRequested)
                {
                    delayCancellation = new CancellationTokenSource();
                    _delayCancellation = delayCancellation;
                }
                var delayTask = Task.Delay(delay, _timeProvider, delayCancellation.Token);
                if (await Task.WhenAny(pendingRead, delayTask).ConfigureAwait(false) == pendingRead)
                {
                    _pendingReadWait = null;
                    delayCancellation.Cancel();
                    return await pendingRead.ConfigureAwait(false);
                }

                if (remaining <= MaximumTimerDelay)
                    return false;
            }
        }

        private ValueTask<bool> WaitToReadAsync()
        {
            var pendingRead = _pendingReadWait;
            if (pendingRead is null)
                return _queue.Reader.WaitToReadAsync(CancellationToken.None);

            _pendingReadWait = null;
            return new ValueTask<bool>(pendingRead);
        }

        private bool TryReserve(int bytes)
        {
            if (bytes < 0)
                return false;
            if (bytes == 0)
                return true;

            while (true)
            {
                var current = Volatile.Read(ref _queuedBytes);
                var canReserve = bytes <= _maxQueuedBytes
                    ? current <= _maxQueuedBytes - bytes
                    : current == 0;
                if (!canReserve)
                    return false;
                if (Interlocked.CompareExchange(ref _queuedBytes, current + bytes, current) == current)
                {
                    SharpLinkTelemetry.AddSendQueueBytes(bytes);
                    return true;
                }
            }
        }

        private async ValueTask ReserveAsync(int bytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopped) != 0)
                    throw CreateTransportClosedException();
                if (TryReserve(bytes))
                    return;

                Task waitTask;
                lock (_admissionGate)
                {
                    if (Volatile.Read(ref _stopped) != 0)
                        throw CreateTransportClosedException();
                    if (TryReserve(bytes))
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
            while (_queue.Reader.TryRead(out var frame))
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
            _queue.Writer.TryComplete();
            PulseCapacityWaiters();
        }

        private void ReportFaultOnce(Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) != 0)
                return;
            Interlocked.Exchange(ref _stopped, 1);
            _queue.Writer.TryComplete(exception);
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
