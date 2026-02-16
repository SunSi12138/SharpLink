namespace SharpLink.Runtime;

public sealed partial class RpcSession
{
    private sealed class SendPump(PipeWriter output, int flushSizeThreshold, TimeSpan maxLatency, CancellationTokenSource cts) : IDisposable
    {
        private readonly ConcurrentQueue<ArrayBufferWriter<byte>> _q = new();
        private readonly TaskCompletionSource<bool> _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _wip;
        private int _activeDrains;
        private bool _disposed;

        private readonly long _maxLatencyTicks = Math.Max(1L, (long)(maxLatency.TotalSeconds * Stopwatch.Frequency));

        public void Enqueue(ArrayBufferWriter<byte> packet)
        {
            if (Volatile.Read(ref _disposed))
            {
                BufferWriterPool.Return(packet);
                return;
            }

            _q.Enqueue(packet);

            // 只有 0->1 时启动一次 drain
            if (Interlocked.Increment(ref _wip) != 1) return;
            Interlocked.Increment(ref _activeDrains);
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var self = (SendPump)state!;
                _ = self.DrainAsync();
            }, this);
        }

        private async Task DrainAsync()
        {
            var missed = 1;
            var bytesAccumulated = 0;
            var batchStart = Stopwatch.GetTimestamp();

            try
            {
                while (true)
                {
                    while (_q.TryDequeue(out var buffer))
                    {
                        if (Volatile.Read(ref _disposed))
                        {
                            BufferWriterPool.Return(buffer);
                            continue;
                        }

                        var span = buffer.WrittenSpan;
                        if (!span.IsEmpty)
                        {
                            var dest = output.GetSpan(span.Length);
                            span.CopyTo(dest);
                            output.Advance(span.Length);
                            bytesAccumulated += span.Length;
                        }

                        BufferWriterPool.Return(buffer);

                        if (bytesAccumulated < flushSizeThreshold)
                        {
                            var now = Stopwatch.GetTimestamp();
                            if ((now - batchStart) < _maxLatencyTicks)
                                continue;

                            var timedFlush = await output.FlushAsync(cts.Token).ConfigureAwait(false);
                            if (timedFlush.IsCanceled || timedFlush.IsCompleted)
                                return;

                            bytesAccumulated = 0;
                            batchStart = now;
                            continue;
                        }

                        var sizeFlush = await output.FlushAsync(cts.Token).ConfigureAwait(false);
                        if (sizeFlush.IsCanceled || sizeFlush.IsCompleted)
                            return;

                        bytesAccumulated = 0;
                        batchStart = Stopwatch.GetTimestamp();
                    }

                    if (bytesAccumulated > 0)
                    {
                        var flush = await output.FlushAsync(cts.Token).ConfigureAwait(false);
                        if (flush.IsCanceled || flush.IsCompleted)
                            return;

                        bytesAccumulated = 0;
                        batchStart = Stopwatch.GetTimestamp();
                    }

                    var w = Interlocked.Add(ref _wip, -missed);
                    if (w == 0)
                        break;

                    missed = w;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeDrains) == 0 && Volatile.Read(ref _disposed))
                    _stoppedTcs.TrySetResult(true);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
                return;

            // 把队列里没发送的 buffer 全归还池
            while (_q.TryDequeue(out var buf))
                BufferWriterPool.Return(buf);

            if (Volatile.Read(ref _activeDrains) == 0)
                _stoppedTcs.TrySetResult(true);
        }

        public ValueTask WaitForStopAsync()
            => _stoppedTcs.Task.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(_stoppedTcs.Task);
    }
}
