using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace SharpLink.LoadTestBase;

/// <summary>Captures process and shared-memory transport deltas for one load-test stage.</summary>
public sealed class PerformanceEvidenceCollector : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly MeterListener _listener = new();
    private long _spillBytes;
    private long _waits;
    private long _notifications;
    private long _negotiatedCapacity;
    private string? _notificationBackend;

    public PerformanceEvidenceCollector()
    {
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name.StartsWith("sharplink.shared_memory.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    public PerformanceEvidenceSnapshot Capture()
    {
        _process.Refresh();
        return new PerformanceEvidenceSnapshot(
            _process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            Interlocked.Read(ref _spillBytes),
            Interlocked.Read(ref _waits),
            Interlocked.Read(ref _notifications),
            Interlocked.Read(ref _negotiatedCapacity),
            Volatile.Read(ref _notificationBackend));
    }

    public static PerformanceStageEvidence Delta(
        in PerformanceEvidenceSnapshot before,
        in PerformanceEvidenceSnapshot after)
        => new(
            Math.Max(0, after.CpuMilliseconds - before.CpuMilliseconds),
            Math.Max(0, after.AllocatedBytes - before.AllocatedBytes),
            Math.Max(0, after.Gen0Collections - before.Gen0Collections),
            Math.Max(0, after.Gen1Collections - before.Gen1Collections),
            Math.Max(0, after.Gen2Collections - before.Gen2Collections),
            Math.Max(0, after.SharedMemorySpillBytes - before.SharedMemorySpillBytes),
            Math.Max(0, after.SharedMemoryWaits - before.SharedMemoryWaits),
            Math.Max(0, after.SharedMemoryNotifications - before.SharedMemoryNotifications),
            after.NegotiatedCapacityPerDirectionBytes == 0
                ? null
                : checked((int)after.NegotiatedCapacityPerDirectionBytes),
            after.NotificationBackend);

    private void OnMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        _ = state;
        switch (instrument.Name)
        {
            case "sharplink.shared_memory.spill.bytes":
                Interlocked.Add(ref _spillBytes, measurement);
                break;
            case "sharplink.shared_memory.waits":
                Interlocked.Add(ref _waits, measurement);
                break;
            case "sharplink.shared_memory.notifications":
                Interlocked.Add(ref _notifications, measurement);
                break;
            case "sharplink.shared_memory.connections":
                foreach (var tag in tags)
                {
                    if (tag.Key == "sharplink.shared_memory.capacity" && tag.Value is int capacity)
                        Interlocked.Exchange(ref _negotiatedCapacity, capacity);
                    else if (tag.Key == "sharplink.shared_memory.notification_backend")
                        Volatile.Write(ref _notificationBackend, tag.Value?.ToString());
                }
                break;
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _process.Dispose();
    }
}

public readonly record struct PerformanceEvidenceSnapshot(
    double CpuMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long SharedMemorySpillBytes,
    long SharedMemoryWaits,
    long SharedMemoryNotifications,
    long NegotiatedCapacityPerDirectionBytes,
    string? NotificationBackend);

public sealed record PerformanceStageEvidence(
    double CpuMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long SharedMemorySpillBytes,
    long SharedMemoryWaits,
    long SharedMemoryNotifications,
    int? NegotiatedCapacityPerDirectionBytes,
    string? NotificationBackend);
