namespace SharpLink.Runtime;

public sealed partial class RpcSession
{
    private sealed class SendPump(PipeWriter output, int flushSizeThreshold, TimeSpan maxLatency, CancellationTokenSource cts) : IDisposable
    {
        private readonly ConcurrentQueue<ArrayBufferWriter<byte>> _q = new();

        private int _wip;
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
            if (Interlocked.Increment(ref _wip) == 1)
            {
                // 关键：把 drain 放到线程池，避免在 Enqueue 线程同步跑 drain
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    var self = (SendPump)state!;
                    _ = self.DrainAsync();
                }, this);
            }
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
                        var r = await output.FlushAsync(cts.Token).ConfigureAwait(false);
                        if (r.IsCanceled || r.IsCompleted) return;

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
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
                return;

            // 把队列里没发送的 buffer 全归还池
            while (_q.TryDequeue(out var buf))
                BufferWriterPool.Return(buf);

            // 注意：_cts 由外层 RpcSession 管，别在这里 Dispose
        }
    }
}
