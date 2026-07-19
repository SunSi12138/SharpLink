using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkTelemetryTests
{
    [Test]
    public void AbandonedAndLateResponseMetricsShouldExposeStableTags()
    {
        const string side = "telemetry-unit-test";
        var abandoned = 0L;
        var lateDropped = 0L;
        string? terminationReason = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name is
                "sharplink.calls.abandoned" or "sharplink.responses.late_dropped")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var matchingSide = false;
            string? measuredReason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && Equals(tag.Value, side))
                    matchingSide = true;
                else if (tag.Key == "rpc.sharplink.termination_reason")
                    measuredReason = tag.Value as string;
            }
            if (!matchingSide)
                return;
            if (instrument.Name == "sharplink.calls.abandoned")
            {
                Interlocked.Add(ref abandoned, measurement);
                terminationReason = measuredReason;
            }
            else if (instrument.Name == "sharplink.responses.late_dropped")
                Interlocked.Add(ref lateDropped, measurement);
        });
        listener.Start();

        SharpLinkTelemetry.RecordAbandonedCall(side, "deadline_exceeded");
        SharpLinkTelemetry.RecordLateResponseDropped(side);

        Ensure(Volatile.Read(ref abandoned) == 1, "abandoned measurement");
        Ensure(Volatile.Read(ref lateDropped) == 1, "late response measurement");
        Ensure(terminationReason == "deadline_exceeded", "termination reason tag");
    }

    [Test]
    public void SharedMemoryEvidenceMetricsShouldExposeStableKindsAndReasons()
    {
        var directBytes = 0L;
        var wrapSpillBytes = 0L;
        var spillCopyBytes = 0L;
        var stagingBytes = 0L;
        var stagingCopyBytes = 0L;
        var notificationRequests = 0L;
        var notificationCoalesced = 0L;
        var notifications = 0L;
        var cursorRefreshes = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name.StartsWith("sharplink.shared_memory.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            switch (instrument.Name)
            {
                case "sharplink.shared_memory.direct_write.bytes":
                    Interlocked.Add(ref directBytes, measurement);
                    break;
                case "sharplink.shared_memory.spill.bytes":
                    if (FindTag(tags, "sharplink.shared_memory.spill_reason") == "wrap")
                        Interlocked.Add(ref wrapSpillBytes, measurement);
                    break;
                case "sharplink.shared_memory.spill.copy.bytes":
                    Interlocked.Add(ref spillCopyBytes, measurement);
                    break;
                case "sharplink.shared_memory.staging.bytes":
                    Interlocked.Add(ref stagingBytes, measurement);
                    break;
                case "sharplink.shared_memory.staging.copy.bytes":
                    Interlocked.Add(ref stagingCopyBytes, measurement);
                    break;
                case "sharplink.shared_memory.notification.requests":
                    if (FindTag(tags, "sharplink.shared_memory.notification_kind") == "data")
                        Interlocked.Add(ref notificationRequests, measurement);
                    break;
                case "sharplink.shared_memory.notification.coalesced":
                    if (FindTag(tags, "sharplink.shared_memory.notification_kind") == "data")
                        Interlocked.Add(ref notificationCoalesced, measurement);
                    break;
                case "sharplink.shared_memory.notifications":
                    if (FindTag(tags, "sharplink.shared_memory.notification_kind") == "space")
                        Interlocked.Add(ref notifications, measurement);
                    break;
                case "sharplink.shared_memory.cursor.refreshes":
                    if (FindTag(tags, "sharplink.shared_memory.cursor_kind") == "writer_read")
                        Interlocked.Add(ref cursorRefreshes, measurement);
                    break;
            }
        });
        listener.Start();

        SharpLinkTelemetry.RecordSharedMemoryDirectWriteBytes(11);
        SharpLinkTelemetry.RecordSharedMemorySpillBytes(13, "wrap");
        SharpLinkTelemetry.RecordSharedMemorySpillCopyBytes(17);
        SharpLinkTelemetry.RecordSharedMemoryStagingBytes(19);
        SharpLinkTelemetry.RecordSharedMemoryStagingCopyBytes(23);
        SharpLinkTelemetry.RecordSharedMemoryNotificationRequest("data");
        SharpLinkTelemetry.RecordSharedMemoryNotificationCoalesced("data");
        SharpLinkTelemetry.RecordSharedMemoryNotification("space");
        SharpLinkTelemetry.RecordSharedMemoryCursorRefresh("writer_read");

        Ensure(Volatile.Read(ref directBytes) == 11, "shared-memory direct bytes");
        Ensure(Volatile.Read(ref wrapSpillBytes) == 13, "shared-memory wrap spill bytes");
        Ensure(Volatile.Read(ref spillCopyBytes) == 17, "shared-memory spill copy bytes");
        Ensure(Volatile.Read(ref stagingBytes) == 19, "shared-memory staging bytes");
        Ensure(Volatile.Read(ref stagingCopyBytes) == 23, "shared-memory staging copy bytes");
        Ensure(Volatile.Read(ref notificationRequests) == 1, "shared-memory notification requests");
        Ensure(Volatile.Read(ref notificationCoalesced) == 1, "shared-memory coalesced notifications");
        Ensure(Volatile.Read(ref notifications) == 1, "shared-memory written notifications");
        Ensure(Volatile.Read(ref cursorRefreshes) == 1, "shared-memory cursor refreshes");
    }

    private static string? FindTag(
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
