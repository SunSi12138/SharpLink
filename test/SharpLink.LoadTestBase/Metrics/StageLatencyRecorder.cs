using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpLink.LoadTestBase;

/// <summary>
/// Stores exact formal latency samples in bounded, logical-worker-owned buffers.
/// Recording must finish before <see cref="Complete"/> is called.
/// </summary>
public sealed class StageLatencyRecorder
{
    public const string Version = "worker-local-raw-v1";

    private readonly WorkerLatencyRecorder[] _workers;
    private readonly long _stopwatchFrequency;

    public StageLatencyRecorder(
        int workerCount,
        int maximumTotalSamples,
        long stopwatchFrequency = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        if (maximumTotalSamples < workerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTotalSamples),
                maximumTotalSamples,
                "The total sample capacity must provide at least one slot per worker.");
        }

        _stopwatchFrequency = stopwatchFrequency == 0
            ? Stopwatch.Frequency
            : stopwatchFrequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_stopwatchFrequency);

        MaximumTotalSamples = maximumTotalSamples;
        _workers = new WorkerLatencyRecorder[workerCount];
        var baseCapacity = maximumTotalSamples / workerCount;
        var extraCapacity = maximumTotalSamples % workerCount;
        for (var worker = 0; worker < workerCount; worker++)
        {
            var capacity = baseCapacity + (worker < extraCapacity ? 1 : 0);
            _workers[worker] = new WorkerLatencyRecorder(worker, capacity);
        }
    }

    public int WorkerCount => _workers.Length;

    public int MaximumTotalSamples { get; }

    public long StopwatchFrequency => _stopwatchFrequency;

    public WorkerLatencyRecorder GetWorker(int workerIndex)
        => _workers[workerIndex];

    public LatencyStatistics Complete()
    {
        var total = 0;
        foreach (var worker in _workers)
            total = checked(total + worker.Count);

        if (total == 0)
            return LatencyStatistics.Empty;

        var samples = new long[total];
        var destination = 0;
        foreach (var worker in _workers)
        {
            worker.CopyTo(samples.AsSpan(destination, worker.Count));
            destination += worker.Count;
        }

        Array.Sort(samples);
        var sum = 0d;
        foreach (var ticks in samples)
            sum += TicksToMicroseconds(ticks);

        return new LatencyStatistics(
            total,
            TicksToMicroseconds(samples[0]),
            TicksToMicroseconds(samples[^1]),
            sum / total,
            Percentile(samples, 50),
            Percentile(samples, 95),
            Percentile(samples, 99),
            Percentile(samples, 99.9));
    }

    public double TicksToMicroseconds(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return ticks * 1_000_000d / _stopwatchFrequency;
    }

    private double Percentile(long[] sortedSamples, double percentile)
    {
        var rank = decimal.ToInt32(decimal.Ceiling(
            sortedSamples.Length * ((decimal)percentile / 100m)));
        var index = Math.Clamp(rank - 1, 0, sortedSamples.Length - 1);
        return TicksToMicroseconds(sortedSamples[index]);
    }
}

/// <summary>A bounded latency buffer owned by one logical workload worker.</summary>
public sealed class WorkerLatencyRecorder
{
    private readonly int _workerIndex;
    private readonly long[] _elapsedTicks;
    private int _count;

    internal WorkerLatencyRecorder(int workerIndex, int capacity)
    {
        _workerIndex = workerIndex;
        _elapsedTicks = GC.AllocateUninitializedArray<long>(capacity);
    }

    public int Capacity => _elapsedTicks.Length;

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordTicks(int logicalWorkerIndex, long elapsedTicks)
    {
        if (logicalWorkerIndex != _workerIndex)
        {
            throw new InvalidOperationException(
                $"Latency recorder {_workerIndex} cannot be written by logical worker {logicalWorkerIndex}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        if (_count >= _elapsedTicks.Length)
        {
            throw new LatencySampleCapacityExceededException(
                $"Formal latency sample capacity {_elapsedTicks.Length} was exhausted for worker {_workerIndex}; the run is invalid.");
        }

        _elapsedTicks[_count++] = elapsedTicks;
    }

    internal void CopyTo(Span<long> destination)
    {
        if (destination.Length != _count)
            throw new ArgumentException("Destination length must equal the recorded sample count.", nameof(destination));
        _elapsedTicks.AsSpan(0, _count).CopyTo(destination);
    }
}

public sealed class LatencySampleCapacityExceededException : InvalidOperationException
{
    public LatencySampleCapacityExceededException(string message)
        : base(message)
    {
    }
}

public readonly record struct LatencyStatistics(
    long Count,
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
