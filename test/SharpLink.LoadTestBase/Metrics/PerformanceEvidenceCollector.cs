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
    private long _directWriteBytes;
    private long _spillBytes;
    private long _spillWrapBytes;
    private long _spillBackpressureBytes;
    private long _spillPendingBytes;
    private long _spillCopyBytes;
    private long _stagingBytes;
    private long _stagingCopyBytes;
    private long _waits;
    private long _notificationRequests;
    private long _notificationCoalesced;
    private long _notifications;
    private long _cursorRefreshes;
    private long _negotiatedCapacity;
    private string? _notificationBackend;

    public PerformanceEvidenceCollector(bool detailedSharedMemory = false)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                (IsCoreSharedMemoryInstrument(instrument.Name) ||
                 detailedSharedMemory &&
                 instrument.Name.StartsWith("sharplink.shared_memory.", StringComparison.Ordinal)))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    private static bool IsCoreSharedMemoryInstrument(string name)
        => name is
            "sharplink.shared_memory.connections" or
            "sharplink.shared_memory.spill.bytes" or
            "sharplink.shared_memory.waits" or
            "sharplink.shared_memory.notifications";

    public PerformanceEvidenceSnapshot Capture()
    {
        _process.Refresh();
        return new PerformanceEvidenceSnapshot(
            _process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            Interlocked.Read(ref _directWriteBytes),
            Interlocked.Read(ref _spillBytes),
            Interlocked.Read(ref _spillWrapBytes),
            Interlocked.Read(ref _spillBackpressureBytes),
            Interlocked.Read(ref _spillPendingBytes),
            Interlocked.Read(ref _spillCopyBytes),
            Interlocked.Read(ref _stagingBytes),
            Interlocked.Read(ref _stagingCopyBytes),
            Interlocked.Read(ref _waits),
            Interlocked.Read(ref _notificationRequests),
            Interlocked.Read(ref _notificationCoalesced),
            Interlocked.Read(ref _notifications),
            Interlocked.Read(ref _cursorRefreshes),
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
            Math.Max(0, after.SharedMemoryDirectWriteBytes - before.SharedMemoryDirectWriteBytes),
            Math.Max(0, after.SharedMemorySpillBytes - before.SharedMemorySpillBytes),
            Math.Max(0, after.SharedMemorySpillWrapBytes - before.SharedMemorySpillWrapBytes),
            Math.Max(0, after.SharedMemorySpillBackpressureBytes - before.SharedMemorySpillBackpressureBytes),
            Math.Max(0, after.SharedMemorySpillPendingBytes - before.SharedMemorySpillPendingBytes),
            Math.Max(0, after.SharedMemorySpillCopyBytes - before.SharedMemorySpillCopyBytes),
            Math.Max(0, after.SharedMemoryStagingBytes - before.SharedMemoryStagingBytes),
            Math.Max(0, after.SharedMemoryStagingCopyBytes - before.SharedMemoryStagingCopyBytes),
            Math.Max(0, after.SharedMemoryWaits - before.SharedMemoryWaits),
            Math.Max(0, after.SharedMemoryNotificationRequests - before.SharedMemoryNotificationRequests),
            Math.Max(0, after.SharedMemoryNotificationCoalesced - before.SharedMemoryNotificationCoalesced),
            Math.Max(0, after.SharedMemoryNotifications - before.SharedMemoryNotifications),
            Math.Max(0, after.SharedMemoryCursorRefreshes - before.SharedMemoryCursorRefreshes),
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
            case "sharplink.shared_memory.direct_write.bytes":
                Interlocked.Add(ref _directWriteBytes, measurement);
                break;
            case "sharplink.shared_memory.spill.bytes":
                Interlocked.Add(ref _spillBytes, measurement);
                switch (GetStringTag(tags, "sharplink.shared_memory.spill_reason"))
                {
                    case "wrap":
                        Interlocked.Add(ref _spillWrapBytes, measurement);
                        break;
                    case "backpressure":
                        Interlocked.Add(ref _spillBackpressureBytes, measurement);
                        break;
                    case "pending":
                        Interlocked.Add(ref _spillPendingBytes, measurement);
                        break;
                }
                break;
            case "sharplink.shared_memory.spill.copy.bytes":
                Interlocked.Add(ref _spillCopyBytes, measurement);
                break;
            case "sharplink.shared_memory.staging.bytes":
                Interlocked.Add(ref _stagingBytes, measurement);
                break;
            case "sharplink.shared_memory.staging.copy.bytes":
                Interlocked.Add(ref _stagingCopyBytes, measurement);
                break;
            case "sharplink.shared_memory.waits":
                Interlocked.Add(ref _waits, measurement);
                break;
            case "sharplink.shared_memory.notification.requests":
                Interlocked.Add(ref _notificationRequests, measurement);
                break;
            case "sharplink.shared_memory.notification.coalesced":
                Interlocked.Add(ref _notificationCoalesced, measurement);
                break;
            case "sharplink.shared_memory.notifications":
                Interlocked.Add(ref _notifications, measurement);
                break;
            case "sharplink.shared_memory.cursor.refreshes":
                Interlocked.Add(ref _cursorRefreshes, measurement);
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

    private static string? GetStringTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
                return tag.Value as string;
        }
        return null;
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
    long SharedMemoryDirectWriteBytes,
    long SharedMemorySpillBytes,
    long SharedMemorySpillWrapBytes,
    long SharedMemorySpillBackpressureBytes,
    long SharedMemorySpillPendingBytes,
    long SharedMemorySpillCopyBytes,
    long SharedMemoryStagingBytes,
    long SharedMemoryStagingCopyBytes,
    long SharedMemoryWaits,
    long SharedMemoryNotificationRequests,
    long SharedMemoryNotificationCoalesced,
    long SharedMemoryNotifications,
    long SharedMemoryCursorRefreshes,
    long NegotiatedCapacityPerDirectionBytes,
    string? NotificationBackend);

public sealed record PerformanceStageEvidence(
    double CpuMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long SharedMemoryDirectWriteBytes,
    long SharedMemorySpillBytes,
    long SharedMemorySpillWrapBytes,
    long SharedMemorySpillBackpressureBytes,
    long SharedMemorySpillPendingBytes,
    long SharedMemorySpillCopyBytes,
    long SharedMemoryStagingBytes,
    long SharedMemoryStagingCopyBytes,
    long SharedMemoryWaits,
    long SharedMemoryNotificationRequests,
    long SharedMemoryNotificationCoalesced,
    long SharedMemoryNotifications,
    long SharedMemoryCursorRefreshes,
    int? NegotiatedCapacityPerDirectionBytes,
    string? NotificationBackend);
