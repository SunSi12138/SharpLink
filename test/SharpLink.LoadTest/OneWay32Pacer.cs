using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SharpLink.LoadTest;

/// <summary>Limits offered OneWay load without using the RPC queue as a throttle.</summary>
internal sealed class OneWay32Pacer(int batchSize)
{
    internal const int BatchesPerSecond = 50;
    private static readonly TimeSpan s_spacing = TimeSpan.FromSeconds(1d / BatchesPerSecond);
    private long _batchStarted;
    private int _remaining;

    internal ValueTask WaitForPermitAsync()
    {
        if (_remaining > 0)
        {
            _remaining--;
            return ValueTask.CompletedTask;
        }
        return StartBatchAsync();
    }

    private async ValueTask StartBatchAsync()
    {
        if (_batchStarted != 0)
        {
            var remaining = s_spacing - Stopwatch.GetElapsedTime(_batchStarted);
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds))).ConfigureAwait(false);
                remaining = s_spacing - Stopwatch.GetElapsedTime(_batchStarted);
            }
        }

        // A stalled or late worker starts a single new batch; it never accumulates permits.
        _batchStarted = Stopwatch.GetTimestamp();
        _remaining = batchSize - 1;
    }
}
