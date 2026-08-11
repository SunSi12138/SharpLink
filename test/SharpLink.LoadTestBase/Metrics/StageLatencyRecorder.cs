using System;
using System.Diagnostics;

namespace SharpLink.LoadTestBase;

/// <summary>A bounded latency recorder whose hot path is owned by one workload worker.</summary>
public sealed class WorkerLatencyRecorder
{
    private readonly long[] _elapsedTicks;
    private int _count;

    public WorkerLatencyRecorder(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _elapsedTicks = new long[capacity];
    }

    public int Capacity => _elapsedTicks.Length;
    public int Count => _count;

    /// <summary>Records one elapsed duration without allocation, locking, atomics, or conversion.</summary>
    public void RecordTicks(long elapsedTicks)
    {
        if (elapsedTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedTicks), "Elapsed ticks cannot be negative.");
        if (_count == _elapsedTicks.Length)
            throw new LatencyCapacityExceededException();

        _elapsedTicks[_count++] = elapsedTicks;
    }

    internal void CopyTo(Span<long> destination, ref int offset)
    {
        _elapsedTicks.AsSpan(0, _count).CopyTo(destination[offset..]);
        offset = checked(offset + _count);
    }
}

public sealed class LatencyCapacityExceededException : InvalidOperationException
{
    public LatencyCapacityExceededException()
        : base("The formal latency sample capacity was exhausted; the run is invalid.")
    {
    }
}

/// <summary>Preallocates worker-owned buffers and produces exact nearest-rank statistics after drain.</summary>
public sealed class StageLatencyRecorder
{
    private readonly WorkerLatencyRecorder[] _workers;

    public StageLatencyRecorder(int workerCount, int maximumTotalSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalSamples);
        if (maximumTotalSamples < workerCount)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalSamples), "Capacity must provide at least one sample per worker.");

        MaximumTotalSamples = maximumTotalSamples;
        var baseCapacity = maximumTotalSamples / workerCount;
        var remainder = maximumTotalSamples % workerCount;
        _workers = new WorkerLatencyRecorder[workerCount];
        for (var i = 0; i < workerCount; i++)
            _workers[i] = new WorkerLatencyRecorder(baseCapacity + (i < remainder ? 1 : 0));
    }

    public long StopwatchFrequency => Stopwatch.Frequency;
    public int MaximumTotalSamples { get; }
    public WorkerLatencyRecorder GetWorker(int index) => _workers[index];

    public LatencyStatistics Complete()
    {
        var count = 0;
        foreach (var worker in _workers)
            count = checked(count + worker.Count);
        if (count == 0)
            return LatencyStatistics.Empty;

        var samples = new long[count];
        var offset = 0;
        foreach (var worker in _workers)
            worker.CopyTo(samples, ref offset);
        Array.Sort(samples);

        double ToMicroseconds(long ticks) => ticks * 1_000_000d / StopwatchFrequency;
        var sum = 0d;
        foreach (var sample in samples)
            sum += sample;
        double Percentile(double percentile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(count * percentile));
            return ToMicroseconds(samples[rank - 1]);
        }

        return new LatencyStatistics(
            count,
            ToMicroseconds(samples[0]),
            ToMicroseconds(samples[^1]),
            sum * 1_000_000d / StopwatchFrequency / count,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            Percentile(0.999));
    }
}

public readonly record struct LatencyStatistics(
    int Count,
    double MinUs,
    double MaxUs,
    double AverageUs,
    double P50Us,
    double P95Us,
    double P99Us,
    double P999Us)
{
    public static LatencyStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
