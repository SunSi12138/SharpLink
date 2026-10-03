using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpLink.LoadTestBase;

/// <summary>
/// Stores exact formal latency samples in bounded, logical-worker-owned buffers.
/// Recording must finish before <see cref="Complete"/> is called.
/// </summary>
public sealed class StageLatencyRecorder
{
    public const string Version = "worker-local-shared-capacity-v2";

    private readonly WorkerLatencyRecorder[] _workers;
    private readonly long _stopwatchFrequency;
    private readonly object _capacityLock = new();
    private int _rebalancing;

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
            _workers[worker] = new WorkerLatencyRecorder(this, worker, capacity);
        }
    }

    public int WorkerCount => _workers.Length;

    public int MaximumTotalSamples { get; }

    public long StopwatchFrequency => _stopwatchFrequency;

    public WorkerLatencyRecorder GetWorker(int workerIndex)
        => _workers[workerIndex];

    internal bool IsRebalancing => Volatile.Read(ref _rebalancing) != 0;

    internal void Refill(WorkerLatencyRecorder requester)
    {
        lock (_capacityLock)
        {
            if (requester.RemainingCapacity != 0)
                return;

            // A full fence pairs with each writer's local entry fence before any
            // unwritten region changes owner. No global atomic runs per sample.
            Interlocked.Exchange(ref _rebalancing, 1);
            try
            {
                foreach (var worker in _workers)
                {
                    var spinner = new SpinWait();
                    while (worker.IsRecording)
                        spinner.SpinOnce();
                }

                WorkerLatencyRecorder? donor = null;
                foreach (var worker in _workers)
                {
                    if (worker != requester && worker.RemainingCapacity > (donor?.RemainingCapacity ?? 0))
                        donor = worker;
                }

                if (donor is null)
                {
                    throw new LatencySampleCapacityExceededException(
                        $"Formal total latency sample capacity {MaximumTotalSamples} was exhausted; the run is invalid.");
                }

                donor.DonateTo(requester, Math.Max(1, donor.RemainingCapacity / 2));
            }
            finally
            {
                Volatile.Write(ref _rebalancing, 0);
            }
        }
    }

    internal void WaitForRebalance()
    {
        var spinner = new SpinWait();
        while (IsRebalancing)
            spinner.SpinOnce();
    }

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
    private readonly StageLatencyRecorder _owner;
    private readonly int _workerIndex;
    private long[] _elapsedTicks;
    private int _regionStart;
    private int _regionEnd;
    private int _position;
    private int _capacity;
    private int _count;
    private int _isRecording;
    private List<RecordedRegion>? _closedRegions;

    internal WorkerLatencyRecorder(StageLatencyRecorder owner, int workerIndex, int capacity)
    {
        _owner = owner;
        _workerIndex = workerIndex;
        _elapsedTicks = GC.AllocateUninitializedArray<long>(capacity);
        _regionEnd = capacity;
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count => _count;

    internal int RemainingCapacity => _regionEnd - _position;

    internal bool IsRecording => Volatile.Read(ref _isRecording) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordTicks(int logicalWorkerIndex, long elapsedTicks)
    {
        if (logicalWorkerIndex != _workerIndex)
        {
            throw new InvalidOperationException(
                $"Latency recorder {_workerIndex} cannot be written by logical worker {logicalWorkerIndex}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        while (true)
        {
            if (_owner.IsRebalancing)
                _owner.WaitForRebalance();

            // This worker-local full fence prevents a rebalance from missing
            // entry while the writer misses the pause through store buffering.
            Interlocked.Exchange(ref _isRecording, 1);
            try
            {
                if (_owner.IsRebalancing)
                    continue;

                if (_position < _regionEnd)
                {
                    _elapsedTicks[_position++] = elapsedTicks;
                    _count++;
                    return;
                }
            }
            finally
            {
                Volatile.Write(ref _isRecording, 0);
            }

            // Never retain the recording flag while taking the global slow
            // path: another rebalance may already be waiting for this worker.
            _owner.Refill(this);
        }
    }

    internal void DonateTo(WorkerLatencyRecorder requester, int length)
    {
        // Prepare metadata before publishing either side of the transfer.
        (requester._closedRegions ??= []).Add(new RecordedRegion(
            requester._elapsedTicks, requester._regionStart, requester._position - requester._regionStart));

        var end = _regionEnd;
        _regionEnd -= length;
        _capacity -= length;
        requester._elapsedTicks = _elapsedTicks;
        requester._regionStart = end - length;
        requester._position = requester._regionStart;
        requester._regionEnd = end;
        requester._capacity += length;
    }

    internal void CopyTo(Span<long> destination)
    {
        if (destination.Length != _count)
            throw new ArgumentException("Destination length must equal the recorded sample count.", nameof(destination));
        var offset = 0;
        if (_closedRegions is not null)
        {
            foreach (var region in _closedRegions)
            {
                region.Buffer.AsSpan(region.Start, region.Length).CopyTo(destination[offset..]);
                offset += region.Length;
            }
        }
        _elapsedTicks.AsSpan(_regionStart, _position - _regionStart).CopyTo(destination[offset..]);
    }

    private readonly record struct RecordedRegion(long[] Buffer, int Start, int Length);
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
