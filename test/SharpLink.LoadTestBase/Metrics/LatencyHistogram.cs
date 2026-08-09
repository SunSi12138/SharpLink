using System;
using System.Threading;

namespace SharpLink.LoadTestBase;

public sealed class LatencyHistogram
{
    private const int DefaultBucketCount = 2_000_000;
    private readonly long[] _buckets;
    private long _count;
    private long _sumUs;
    private long _minUs = long.MaxValue;
    private long _maxUs;

    public LatencyHistogram(int bucketCount = DefaultBucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);
        _buckets = new long[bucketCount];
    }

    public void Record(double microseconds)
    {
        var us = (long)Math.Max(0, Math.Round(microseconds));
        var bucket = (int)Math.Clamp(us, 0, _buckets.Length - 1);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumUs, us);
        UpdateMin(us);
        UpdateMax(us);
    }

    public double Percentile(double p)
    {
        var count = Interlocked.Read(ref _count);
        if (count <= 0)
            return 0;

        var target = (long)Math.Ceiling(count * (p / 100.0));
        long running = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            running += Interlocked.Read(ref _buckets[i]);
            if (running >= target)
                return i;
        }

        return _buckets.Length - 1;
    }

    public double Average
    {
        get
        {
            var count = Interlocked.Read(ref _count);
            if (count <= 0)
                return 0;
            return Interlocked.Read(ref _sumUs) / (double)count;
        }
    }

    public double Min
    {
        get
        {
            var value = Interlocked.Read(ref _minUs);
            return value == long.MaxValue ? 0 : value;
        }
    }

    public double Max => Interlocked.Read(ref _maxUs);

    private void UpdateMin(long value)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _minUs);
            if (value >= old)
                return;
            if (Interlocked.CompareExchange(ref _minUs, value, old) == old)
                return;
        }
    }

    private void UpdateMax(long value)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _maxUs);
            if (value <= old)
                return;
            if (Interlocked.CompareExchange(ref _maxUs, value, old) == old)
                return;
        }
    }
}
