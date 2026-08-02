using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkTelemetryTests
{
    [Test]
    public void RemoteResourceExhaustionShouldRestoreKnownReasonFromWireMessage()
    {
        var wire = SharpLinkResourceExhaustion.CreateWire(
            SharpLinkResourceExhaustion.ServerCallCapacity,
            "Server call capacity is exhausted (server_call_capacity).");
        Ensure(Encoding.UTF8.GetByteCount(wire.Message.AsSpan(0, 1)) == 1,
            "the stable discriminator must survive a one-byte error-message limit");
        var truncated = SharpLinkResourceExhaustion.CreateRemote(
            SharpLinkErrorCode.ResourceExhausted,
            wire.Message[..1]);
        Ensure(
            SharpLinkResourceExhaustion.GetReason(truncated) ==
            SharpLinkResourceExhaustion.ServerCallCapacity,
            "a maximally truncated wire message must retain its stable reason");

        var restored = SharpLinkResourceExhaustion.CreateRemote(
            SharpLinkErrorCode.ResourceExhausted,
            "Server call capacity is exhausted (server_call_capacity).");
        Ensure(
            SharpLinkResourceExhaustion.GetReason(restored) ==
            SharpLinkResourceExhaustion.ServerCallCapacity,
            "the client must restore the server-provided stable reason after wire decoding");

        var unspecified = SharpLinkResourceExhaustion.CreateRemote(
            SharpLinkErrorCode.ResourceExhausted,
            "An older peer reported an unclassified bounded-resource failure.");
        Ensure(
            SharpLinkResourceExhaustion.GetReason(unspecified) ==
            SharpLinkResourceExhaustion.Unspecified,
            "unknown peer messages must remain a bounded unspecified telemetry series");
    }

    [Test]
    public void ResourceExhaustedMetricsShouldExposeStableReasons()
    {
        const string side = "resource-exhaustion-reason-test";
        var measurements = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.resource_exhausted")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (FindTag(tags, "rpc.side") != side)
                return;
            var reason = FindTag(tags, "rpc.sharplink.resource_exhaustion_reason");
            if (reason is null)
                return;
            measurements.TryGetValue(reason, out var current);
            measurements[reason] = current + measurement;
        });
        listener.Start();

        var expectedReasons = new[]
        {
            SharpLinkResourceExhaustion.ServerCallCapacity,
            SharpLinkResourceExhaustion.PerConnectionCallCapacity,
            SharpLinkResourceExhaustion.AdmissionConcurrency,
            SharpLinkResourceExhaustion.AdmissionQueue,
            SharpLinkResourceExhaustion.PendingRequestCapacity,
            SharpLinkResourceExhaustion.SendQueueCapacity
        };
        foreach (var reason in expectedReasons)
            SharpLinkTelemetry.RecordResourceExhausted(side, reason);

        Ensure(measurements.Count == expectedReasons.Length,
            "resource exhaustion must expose one stable series per requested capacity reason");
        foreach (var reason in expectedReasons)
        {
            Ensure(measurements.TryGetValue(reason, out var count) && count == 1,
                $"resource exhaustion reason {reason}");
        }
    }

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

    [Test]
    public void AdmissionMetricsShouldExposeStableNamesAndLowCardinalityReasons()
    {
        var permits = 0L;
        var queued = 0L;
        var rejected = 0L;
        var dropped = 0L;
        var partitions = 0L;
        var duration = 0d;
        string? rejectionScope = null;
        string? rejectionReason = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name.StartsWith("sharplink.admission.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            switch (instrument.Name)
            {
                case "sharplink.admission.permits.active":
                    Interlocked.Add(ref permits, measurement);
                    break;
                case "sharplink.admission.calls.queued":
                    Interlocked.Add(ref queued, measurement);
                    break;
                case "sharplink.admission.calls.rejected":
                    Interlocked.Add(ref rejected, measurement);
                    rejectionScope = FindTag(tags, "sharplink.admission.scope");
                    rejectionReason = FindTag(tags, "sharplink.admission.reason");
                    break;
                case "sharplink.admission.oneway.dropped":
                    Interlocked.Add(ref dropped, measurement);
                    break;
                case "sharplink.admission.partitions.active":
                    Interlocked.Add(ref partitions, measurement);
                    break;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "sharplink.admission.queue.duration")
                duration = measurement;
        });
        listener.Start();

        SharpLinkTelemetry.AddAdmissionActivePermits(1);
        SharpLinkTelemetry.AddAdmissionQueuedCalls(1);
        SharpLinkTelemetry.RecordAdmissionRejected("method", "concurrency");
        SharpLinkTelemetry.RecordAdmissionQueueDuration(TimeSpan.FromMilliseconds(25));
        SharpLinkTelemetry.RecordAdmissionOneWayDropped("method", "concurrency");
        SharpLinkTelemetry.AddAdmissionActivePartitions(1);

        Ensure(Volatile.Read(ref permits) == 1, "admission permits metric");
        Ensure(Volatile.Read(ref queued) == 1, "admission queue metric");
        Ensure(Volatile.Read(ref rejected) == 1, "admission rejected metric");
        Ensure(Volatile.Read(ref dropped) == 1, "admission OneWay metric");
        Ensure(Volatile.Read(ref partitions) == 1, "admission partitions metric");
        Ensure(Math.Abs(duration - 0.025d) < 0.0001d, "admission queue duration seconds");
        Ensure(rejectionScope == "method" && rejectionReason == "concurrency",
            "admission rejection low-cardinality tags");
    }

    [Test]
    public void ClientTopologyMetricsShouldExposeStableLowCardinalityInstruments()
    {
        var resolverUpdates = 0L;
        var resolverFailures = 0L;
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name.StartsWith("sharplink.client.", StringComparison.Ordinal))
            {
                instruments.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "sharplink.client.resolver.updates":
                    Interlocked.Add(ref resolverUpdates, measurement);
                    break;
                case "sharplink.client.resolver.failures":
                    Interlocked.Add(ref resolverFailures, measurement);
                    break;
            }
        });
        listener.Start();

        SharpLinkTelemetry.AddClientActiveEndpoints(2);
        SharpLinkTelemetry.AddClientReadyEndpoints(1);
        SharpLinkTelemetry.AddClientDrainingEndpoints(1);
        SharpLinkTelemetry.RecordClientResolverUpdate();
        SharpLinkTelemetry.RecordClientResolverFailure();
        SharpLinkTelemetry.ConnectionOpened("client");
        SharpLinkTelemetry.AddClientRetiringConnections(1);
        listener.RecordObservableInstruments();
        SharpLinkTelemetry.AddClientActiveEndpoints(-2);
        SharpLinkTelemetry.AddClientReadyEndpoints(-1);
        SharpLinkTelemetry.AddClientDrainingEndpoints(-1);
        SharpLinkTelemetry.AddClientRetiringConnections(-1);
        SharpLinkTelemetry.ConnectionClosed("client");
        listener.RecordObservableInstruments();

        Ensure(instruments.Contains("sharplink.client.endpoints.active"), "active endpoints instrument");
        Ensure(instruments.Contains("sharplink.client.endpoints.ready"), "ready endpoints instrument");
        Ensure(instruments.Contains("sharplink.client.endpoints.draining"), "draining endpoints instrument");
        Ensure(instruments.Contains("sharplink.client.resolver.updates"), "resolver updates instrument");
        Ensure(instruments.Contains("sharplink.client.resolver.failures"), "resolver failures instrument");
        Ensure(instruments.Contains("sharplink.client.connections.active"), "active connections instrument");
        Ensure(instruments.Contains("sharplink.client.connections.retiring"), "retiring connections instrument");
        Ensure(Volatile.Read(ref resolverUpdates) == 1, "resolver updates metric");
        Ensure(Volatile.Read(ref resolverFailures) == 1, "resolver failures metric");
    }

    [Test]
    public void MultiClusterMutationMetricsShouldExposeStableOperationAndResultTags()
    {
        var mutations = 0L;
        var duration = 0d;
        string? operation = null;
        string? result = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name.StartsWith(
                    "sharplink.client.multicluster.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "sharplink.client.multicluster.mutations")
                return;
            Interlocked.Add(ref mutations, measurement);
            operation = FindTag(tags, "sharplink.multicluster.operation");
            result = FindTag(tags, "sharplink.multicluster.result");
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "sharplink.client.multicluster.mutation.duration")
                duration = measurement;
        });
        listener.Start();

        SharpLinkTelemetry.RecordMultiClusterMutation(
            "replace",
            "success",
            TimeSpan.FromMilliseconds(12.5));

        Ensure(Volatile.Read(ref mutations) == 1, "multi-cluster mutation counter");
        Ensure(Math.Abs(duration - 12.5d) < 0.0001d, "multi-cluster mutation duration milliseconds");
        Ensure(operation == "replace" && result == "success",
            "multi-cluster mutation low-cardinality tags");
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
