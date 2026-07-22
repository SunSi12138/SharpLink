using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace SharpLink.Abstractions;

/// <summary>Exposes SharpLink OpenTelemetry-compatible activity and metric sources.</summary>
public static class SharpLinkTelemetry
{
    /// <summary>Gets the client activity source named <c>SharpLink.Client</c>.</summary>
    public static ActivitySource ClientActivitySource { get; } = new("SharpLink.Client");
    /// <summary>Gets the server activity source named <c>SharpLink.Server</c>.</summary>
    public static ActivitySource ServerActivitySource { get; } = new("SharpLink.Server");
    /// <summary>Gets the process-wide immutable meter named <c>SharpLink</c>.</summary>
    public static Meter Meter { get; } = new("SharpLink");

    private static long _clientActiveEndpointCount;
    private static long _clientReadyEndpointCount;
    private static long _clientDrainingEndpointCount;
    private static long _clientActiveConnectionCount;
    private static long _clientRetiringConnectionCount;

    private static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("sharplink.connections.active", unit: "{connection}");
    private static readonly ObservableUpDownCounter<long> ClientActiveEndpoints =
        Meter.CreateObservableUpDownCounter(
            "sharplink.client.endpoints.active",
            static () => Volatile.Read(ref _clientActiveEndpointCount),
            unit: "{endpoint}");
    private static readonly ObservableUpDownCounter<long> ClientReadyEndpoints =
        Meter.CreateObservableUpDownCounter(
            "sharplink.client.endpoints.ready",
            static () => Volatile.Read(ref _clientReadyEndpointCount),
            unit: "{endpoint}");
    private static readonly ObservableUpDownCounter<long> ClientDrainingEndpoints =
        Meter.CreateObservableUpDownCounter(
            "sharplink.client.endpoints.draining",
            static () => Volatile.Read(ref _clientDrainingEndpointCount),
            unit: "{endpoint}");
    private static readonly Counter<long> ClientResolverUpdates =
        Meter.CreateCounter<long>("sharplink.client.resolver.updates", unit: "{update}");
    private static readonly Counter<long> ClientResolverFailures =
        Meter.CreateCounter<long>("sharplink.client.resolver.failures", unit: "{failure}");
    private static readonly ObservableUpDownCounter<long> ClientActiveConnections =
        Meter.CreateObservableUpDownCounter(
            "sharplink.client.connections.active",
            static () => Volatile.Read(ref _clientActiveConnectionCount),
            unit: "{connection}");
    private static readonly ObservableUpDownCounter<long> ClientRetiringConnections =
        Meter.CreateObservableUpDownCounter(
            "sharplink.client.connections.retiring",
            static () => Volatile.Read(ref _clientRetiringConnectionCount),
            unit: "{connection}");
    private static readonly Counter<long> Reconnects =
        Meter.CreateCounter<long>("sharplink.connections.reconnects", unit: "{attempt}");
    private static readonly Counter<long> StartedCalls =
        Meter.CreateCounter<long>("sharplink.calls.started", unit: "{call}");
    private static readonly Counter<long> CompletedCalls =
        Meter.CreateCounter<long>("sharplink.calls.completed", unit: "{call}");
    private static readonly Counter<long> FailedCalls =
        Meter.CreateCounter<long>("sharplink.calls.failed", unit: "{call}");
    private static readonly UpDownCounter<long> ActiveCalls =
        Meter.CreateUpDownCounter<long>("sharplink.calls.active", unit: "{call}");
    private static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("sharplink.calls.duration", unit: "ms");
    private static readonly Counter<long> SentBytes =
        Meter.CreateCounter<long>("sharplink.transport.bytes.sent", unit: "By");
    private static readonly Counter<long> ReceivedBytes =
        Meter.CreateCounter<long>("sharplink.transport.bytes.received", unit: "By");
    private static readonly UpDownCounter<long> SendQueueBytes =
        Meter.CreateUpDownCounter<long>("sharplink.send.queue.bytes", unit: "By");
    private static readonly UpDownCounter<long> PendingRequests =
        Meter.CreateUpDownCounter<long>("sharplink.requests.pending", unit: "{request}");
    private static readonly UpDownCounter<long> ActiveStreams =
        Meter.CreateUpDownCounter<long>("sharplink.streams.active", unit: "{stream}");
    private static readonly Counter<long> ProtocolFailures =
        Meter.CreateCounter<long>("sharplink.protocol.failures", unit: "{failure}");
    private static readonly Counter<long> AuthenticationFailures =
        Meter.CreateCounter<long>("sharplink.authentication.failures", unit: "{failure}");
    private static readonly Counter<long> ResourceExhausted =
        Meter.CreateCounter<long>("sharplink.resource_exhausted", unit: "{failure}");
    private static readonly Counter<long> AbandonedCalls =
        Meter.CreateCounter<long>("sharplink.calls.abandoned", unit: "{call}");
    private static readonly Counter<long> LateDroppedResponses =
        Meter.CreateCounter<long>("sharplink.responses.late_dropped", unit: "{response}");
    private static readonly Counter<long> ForcedStopCalls =
        Meter.CreateCounter<long>("sharplink.server.stop.unfinished_calls", unit: "{call}");
    private static readonly Counter<long> SharedMemoryConnections =
        Meter.CreateCounter<long>("sharplink.shared_memory.connections", unit: "{connection}");
    private static readonly Counter<long> SharedMemorySpillBytes =
        Meter.CreateCounter<long>("sharplink.shared_memory.spill.bytes", unit: "By");
    private static readonly Counter<long> SharedMemoryDirectWriteBytes =
        Meter.CreateCounter<long>("sharplink.shared_memory.direct_write.bytes", unit: "By");
    private static readonly Counter<long> SharedMemorySpillCopyBytes =
        Meter.CreateCounter<long>("sharplink.shared_memory.spill.copy.bytes", unit: "By");
    private static readonly Counter<long> SharedMemoryStagingBytes =
        Meter.CreateCounter<long>("sharplink.shared_memory.staging.bytes", unit: "By");
    private static readonly Counter<long> SharedMemoryStagingCopyBytes =
        Meter.CreateCounter<long>("sharplink.shared_memory.staging.copy.bytes", unit: "By");
    private static readonly Counter<long> SharedMemoryWaits =
        Meter.CreateCounter<long>("sharplink.shared_memory.waits", unit: "{wait}");
    private static readonly Counter<long> SharedMemoryNotificationRequests =
        Meter.CreateCounter<long>("sharplink.shared_memory.notification.requests", unit: "{notification}");
    private static readonly Counter<long> SharedMemoryNotificationCoalesced =
        Meter.CreateCounter<long>("sharplink.shared_memory.notification.coalesced", unit: "{notification}");
    private static readonly Counter<long> SharedMemoryNotifications =
        Meter.CreateCounter<long>("sharplink.shared_memory.notifications", unit: "{notification}");
    private static readonly Counter<long> SharedMemoryCursorRefreshes =
        Meter.CreateCounter<long>("sharplink.shared_memory.cursor.refreshes", unit: "{refresh}");
    private static readonly UpDownCounter<long> AdmissionActivePermits =
        Meter.CreateUpDownCounter<long>("sharplink.admission.permits.active", unit: "{permit}");
    private static readonly UpDownCounter<long> AdmissionQueuedCalls =
        Meter.CreateUpDownCounter<long>("sharplink.admission.calls.queued", unit: "{call}");
    private static readonly Counter<long> AdmissionRejectedCalls =
        Meter.CreateCounter<long>("sharplink.admission.calls.rejected", unit: "{call}");
    private static readonly Histogram<double> AdmissionQueueDuration =
        Meter.CreateHistogram<double>("sharplink.admission.queue.duration", unit: "s");
    private static readonly Counter<long> AdmissionOneWayDropped =
        Meter.CreateCounter<long>("sharplink.admission.oneway.dropped", unit: "{call}");
    private static readonly UpDownCounter<long> AdmissionActivePartitions =
        Meter.CreateUpDownCounter<long>("sharplink.admission.partitions.active", unit: "{partition}");
    private static readonly Counter<long> ClientAttempts =
        Meter.CreateCounter<long>("sharplink.client.attempts", unit: "{attempt}");
    private static readonly Counter<long> ClientRetries =
        Meter.CreateCounter<long>("sharplink.client.retries", unit: "{retry}");
    private static readonly Counter<long> EndpointAdmissionRejected =
        Meter.CreateCounter<long>("sharplink.client.endpoint_admission.rejected", unit: "{attempt}");
    private static readonly Counter<long> SelectionFailures =
        Meter.CreateCounter<long>("sharplink.client.selection.failures", unit: "{failure}");
    private static readonly Counter<long> BreakerOpen =
        Meter.CreateCounter<long>("sharplink.client.breaker.open", unit: "{rejection}");

    internal static CallScope StartClientCall(RpcMethodDescriptor method)
        => StartCall(ClientActivitySource, ActivityKind.Client, "client", method, requestId: 0);

    /// <summary>Starts telemetry for one physical attempt within a logical client call.</summary>
    /// <remarks>
    /// Attempt activities deliberately do not affect the logical call counters. This lets retry
    /// diagnostics show every network attempt while the normal call metrics remain one-per-call.
    /// </remarks>
    internal static AttemptScope StartClientAttempt(RpcMethodDescriptor method, int attempt)
    {
        if (!ClientActivitySource.HasListeners())
            return default;

        var activity = ClientActivitySource.StartActivity("sharplink.rpc.attempt", ActivityKind.Client);
        if (activity is null)
            return default;
        activity.SetTag("rpc.system", "sharplink");
        activity.SetTag("rpc.sharplink.contract_id", method.ContractId);
        activity.SetTag("rpc.sharplink.method_id", method.MethodId);
        activity.SetTag("rpc.sharplink.method_kind", method.Kind.ToString());
        activity.SetTag("rpc.sharplink.attempt", attempt);
        return new AttemptScope(activity);
    }

    internal static CallScope StartServerCall(RpcMethodDescriptor method, long requestId)
        => StartCall(ServerActivitySource, ActivityKind.Server, "server", method, requestId);

    internal static bool ClientCallsEnabled =>
        ClientActivitySource.HasListeners() || CallMetricsEnabled;

    internal static void ConnectionOpened(string side)
    {
        RecordDelta(ActiveConnections, 1, side);
        if (side == "client")
            Interlocked.Increment(ref _clientActiveConnectionCount);
    }
    internal static void ConnectionClosed(string side)
    {
        RecordDelta(ActiveConnections, -1, side);
        if (side == "client")
            Interlocked.Decrement(ref _clientActiveConnectionCount);
    }
    internal static void AddClientActiveEndpoints(long count)
    {
        if (count != 0)
            Interlocked.Add(ref _clientActiveEndpointCount, count);
    }
    internal static void AddClientReadyEndpoints(long count)
    {
        if (count != 0)
            Interlocked.Add(ref _clientReadyEndpointCount, count);
    }
    internal static void AddClientDrainingEndpoints(long count)
    {
        if (count != 0)
            Interlocked.Add(ref _clientDrainingEndpointCount, count);
    }
    internal static void RecordClientResolverUpdate()
    {
        if (ClientResolverUpdates.Enabled)
            ClientResolverUpdates.Add(1);
    }
    internal static void RecordClientResolverFailure()
    {
        if (ClientResolverFailures.Enabled)
            ClientResolverFailures.Add(1);
    }
    internal static void AddClientRetiringConnections(long count)
    {
        if (count != 0)
            Interlocked.Add(ref _clientRetiringConnectionCount, count);
    }
    internal static void ReconnectAttempt() => Record(Reconnects, 1, "client");
    internal static void RecordSentBytes(long bytes) => RecordPositive(SentBytes, bytes);
    internal static void RecordReceivedBytes(long bytes) => RecordPositive(ReceivedBytes, bytes);
    internal static void AddSendQueueBytes(long bytes) => RecordDelta(SendQueueBytes, bytes);
    internal static void AddPendingRequests(long count) => RecordDelta(PendingRequests, count, "client");
    internal static void AddActiveStreams(long count) => RecordDelta(ActiveStreams, count);
    internal static void RecordProtocolFailure(string side) => Record(ProtocolFailures, 1, side);
    internal static void RecordAuthenticationFailure(string side) => Record(AuthenticationFailures, 1, side);
    internal static void RecordResourceExhausted(string side) => Record(ResourceExhausted, 1, side);
    internal static void RecordAbandonedCall(string side, string terminationReason)
    {
        if (!AbandonedCalls.Enabled)
            return;
        AbandonedCalls.Add(
            1,
            new KeyValuePair<string, object?>("rpc.side", side),
            new KeyValuePair<string, object?>(
                "rpc.sharplink.termination_reason",
                terminationReason));
    }
    internal static void RecordLateResponseDropped(string side)
        => Record(LateDroppedResponses, 1, side);
    internal static void RecordForcedStopCalls(long count) => RecordPositive(ForcedStopCalls, count);
    internal static void AddAdmissionActivePermits(long count)
        => RecordDelta(AdmissionActivePermits, count);
    internal static void AddAdmissionQueuedCalls(long count)
        => RecordDelta(AdmissionQueuedCalls, count);
    internal static void AddAdmissionActivePartitions(long count)
        => RecordDelta(AdmissionActivePartitions, count);
    internal static void RecordAdmissionQueueDuration(TimeSpan duration)
    {
        if (duration >= TimeSpan.Zero && AdmissionQueueDuration.Enabled)
            AdmissionQueueDuration.Record(duration.TotalSeconds);
    }
    internal static void RecordAdmissionRejected(string scope, string reason)
    {
        if (!AdmissionRejectedCalls.Enabled)
            return;
        AdmissionRejectedCalls.Add(
            1,
            new KeyValuePair<string, object?>("sharplink.admission.scope", scope),
            new KeyValuePair<string, object?>("sharplink.admission.reason", reason));
    }
    internal static void RecordAdmissionOneWayDropped(string scope, string reason)
    {
        if (!AdmissionOneWayDropped.Enabled)
            return;
        AdmissionOneWayDropped.Add(
            1,
            new KeyValuePair<string, object?>("sharplink.admission.scope", scope),
            new KeyValuePair<string, object?>("sharplink.admission.reason", reason));
    }
    internal static void RecordClientAttempt()
    {
        if (ClientAttempts.Enabled)
            ClientAttempts.Add(1);
    }
    internal static void RecordClientRetry()
    {
        if (ClientRetries.Enabled)
            ClientRetries.Add(1);
    }
    internal static void RecordEndpointAdmissionRejected(string reason)
    {
        if (EndpointAdmissionRejected.Enabled)
            EndpointAdmissionRejected.Add(1, new KeyValuePair<string, object?>("sharplink.admission.reason", reason));
    }
    internal static void RecordSelectionFailure(string reason)
    {
        if (SelectionFailures.Enabled)
            SelectionFailures.Add(1, new KeyValuePair<string, object?>("sharplink.selection.reason", reason));
    }
    internal static void RecordBreakerOpen()
    {
        if (BreakerOpen.Enabled)
            BreakerOpen.Add(1);
    }
    internal static void RecordSharedMemoryConnection(string side, int capacity)
    {
        if (!SharedMemoryConnections.Enabled)
            return;
        SharedMemoryConnections.Add(
            1,
            new KeyValuePair<string, object?>("rpc.side", side),
            new KeyValuePair<string, object?>("sharplink.shared_memory.capacity", capacity),
            new KeyValuePair<string, object?>("sharplink.shared_memory.notification_backend", "named-pipe-control"));
    }
    internal static void RecordSharedMemoryDirectWriteBytes(long bytes)
        => RecordPositive(SharedMemoryDirectWriteBytes, bytes);
    internal static void RecordSharedMemorySpillBytes(long bytes, string reason)
    {
        if (bytes <= 0 || !SharedMemorySpillBytes.Enabled)
            return;
        SharedMemorySpillBytes.Add(
            bytes,
            new KeyValuePair<string, object?>("sharplink.shared_memory.spill_reason", reason));
    }
    internal static void RecordSharedMemorySpillCopyBytes(long bytes)
        => RecordPositive(SharedMemorySpillCopyBytes, bytes);
    internal static void RecordSharedMemoryStagingBytes(long bytes)
        => RecordPositive(SharedMemoryStagingBytes, bytes);
    internal static void RecordSharedMemoryStagingCopyBytes(long bytes)
        => RecordPositive(SharedMemoryStagingCopyBytes, bytes);
    internal static void RecordSharedMemoryWait(string kind)
    {
        if (SharedMemoryWaits.Enabled)
            SharedMemoryWaits.Add(1, new KeyValuePair<string, object?>("sharplink.shared_memory.wait_kind", kind));
    }
    internal static void RecordSharedMemoryNotificationRequest(string kind)
        => RecordSharedMemoryNotificationMetric(SharedMemoryNotificationRequests, kind);
    internal static void RecordSharedMemoryNotificationCoalesced(string kind)
        => RecordSharedMemoryNotificationMetric(SharedMemoryNotificationCoalesced, kind);
    internal static void RecordSharedMemoryNotification(string kind)
        => RecordSharedMemoryNotificationMetric(SharedMemoryNotifications, kind);
    internal static void RecordSharedMemoryCursorRefresh(string kind)
    {
        if (SharedMemoryCursorRefreshes.Enabled)
            SharedMemoryCursorRefreshes.Add(
                1,
                new KeyValuePair<string, object?>("sharplink.shared_memory.cursor_kind", kind));
    }

    private static void RecordSharedMemoryNotificationMetric(Counter<long> instrument, string kind)
    {
        if (instrument.Enabled)
            instrument.Add(
                1,
                new KeyValuePair<string, object?>("sharplink.shared_memory.notification_kind", kind));
    }

    private static bool CallMetricsEnabled =>
        StartedCalls.Enabled || CompletedCalls.Enabled || FailedCalls.Enabled ||
        ActiveCalls.Enabled || RequestDuration.Enabled || ResourceExhausted.Enabled || AbandonedCalls.Enabled;

    private static CallScope StartCall(
        ActivitySource source,
        ActivityKind kind,
        string side,
        RpcMethodDescriptor method,
        long requestId)
    {
        if (!source.HasListeners() && !CallMetricsEnabled)
            return default;

        Activity? activity = null;
        if (source.HasListeners())
        {
            activity = source.StartActivity("sharplink.rpc", kind);
            if (activity is not null)
            {
                activity.SetTag("rpc.system", "sharplink");
                activity.SetTag("rpc.sharplink.contract_id", method.ContractId);
                activity.SetTag("rpc.sharplink.method_id", method.MethodId);
                activity.SetTag("rpc.sharplink.method_kind", method.Kind.ToString());
                if (requestId != 0)
                    activity.SetTag("rpc.sharplink.request_id", requestId);
            }
        }

        RecordCallMetric(StartedCalls, 1, side, method, status: null);
        RecordCallDelta(ActiveCalls, 1, side, method);
        return new CallScope(side, method, activity, RequestDuration.Enabled ? Stopwatch.GetTimestamp() : 0);
    }

    private static void Record(Counter<long> instrument, long value, string side)
    {
        if (!instrument.Enabled)
            return;
        instrument.Add(value, new KeyValuePair<string, object?>("rpc.side", side));
    }

    private static void RecordPositive(Counter<long> instrument, long value)
    {
        if (value > 0 && instrument.Enabled)
            instrument.Add(value);
    }

    private static void RecordDelta(UpDownCounter<long> instrument, long value)
    {
        if (value != 0 && instrument.Enabled)
            instrument.Add(value);
    }

    private static void RecordDelta(UpDownCounter<long> instrument, long value, string side)
    {
        if (value == 0 || !instrument.Enabled)
            return;
        instrument.Add(value, new KeyValuePair<string, object?>("rpc.side", side));
    }

    private static void RecordCallMetric(
        Counter<long> instrument,
        long value,
        string side,
        RpcMethodDescriptor method,
        SharpLinkErrorCode? status)
    {
        if (!instrument.Enabled)
            return;
        var tags = new TagList
        {
            { "rpc.side", side },
            { "rpc.sharplink.contract_id", method.ContractId },
            { "rpc.sharplink.method_id", method.MethodId }
        };
        if (status is { } code)
            tags.Add("rpc.sharplink.status", code.ToString());
        instrument.Add(value, tags);
    }

    private static void RecordCallDelta(
        UpDownCounter<long> instrument,
        long value,
        string side,
        RpcMethodDescriptor method)
    {
        if (!instrument.Enabled)
            return;
        instrument.Add(
            value,
            new KeyValuePair<string, object?>("rpc.side", side),
            new KeyValuePair<string, object?>("rpc.sharplink.contract_id", method.ContractId),
            new KeyValuePair<string, object?>("rpc.sharplink.method_id", method.MethodId));
    }

    internal struct CallScope
    {
        private readonly string? _side;
        private readonly RpcMethodDescriptor _method;
        private readonly Activity? _activity;
        private readonly long _started;
        private bool _completed;

        internal CallScope(
            string side,
            RpcMethodDescriptor method,
            Activity? activity,
            long started)
        {
            _side = side;
            _method = method;
            _activity = activity;
            _started = started;
            _completed = false;
        }

        internal readonly bool IsEnabled => _side is not null;

        internal void Complete(Exception? exception = null)
        {
            if (_side is null || _completed)
                return;
            _completed = true;

            var status = exception switch
            {
                null => (SharpLinkErrorCode?)null,
                SharpLinkException sharpLinkException => sharpLinkException.Code,
                OperationCanceledException => SharpLinkErrorCode.Cancelled,
                _ => SharpLinkErrorCode.Internal
            };
            if (exception is null)
            {
                RecordCallMetric(CompletedCalls, 1, _side, _method, status: null);
                _activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                RecordCallMetric(FailedCalls, 1, _side, _method, status);
                _activity?.SetStatus(ActivityStatusCode.Error, status?.ToString());
                _activity?.SetTag("error.type", exception.GetType().FullName);
                if (status == SharpLinkErrorCode.ResourceExhausted)
                    RecordResourceExhausted(_side);
            }
            RecordCallDelta(ActiveCalls, -1, _side, _method);
            if (_started != 0 && RequestDuration.Enabled)
            {
                var tags = new TagList
                {
                    { "rpc.side", _side },
                    { "rpc.sharplink.contract_id", _method.ContractId },
                    { "rpc.sharplink.method_id", _method.MethodId },
                    { "rpc.sharplink.status", status?.ToString() ?? "Ok" }
                };
                RequestDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tags);
            }
            _activity?.Dispose();
        }
    }

    internal struct AttemptScope
    {
        private readonly Activity? _activity;
        private bool _completed;

        internal AttemptScope(Activity activity)
        {
            _activity = activity;
        }

        internal void Complete(Exception? exception = null)
        {
            if (_completed)
                return;
            _completed = true;
            if (exception is null)
            {
                _activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                var status = exception switch
                {
                    SharpLinkException sharpLinkException => sharpLinkException.Code.ToString(),
                    OperationCanceledException => SharpLinkErrorCode.Cancelled.ToString(),
                    _ => SharpLinkErrorCode.Internal.ToString()
                };
                _activity?.SetStatus(ActivityStatusCode.Error, status);
                _activity?.SetTag("error.type", exception.GetType().FullName);
            }
            _activity?.Dispose();
        }
    }
}
