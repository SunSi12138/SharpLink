using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    private static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("sharplink.connections.active", unit: "{connection}");
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
    private static readonly Counter<long> ForcedStopCalls =
        Meter.CreateCounter<long>("sharplink.server.stop.unfinished_calls", unit: "{call}");

    internal static CallScope StartClientCall(RpcMethodDescriptor method)
        => StartCall(ClientActivitySource, ActivityKind.Client, "client", method, requestId: 0);

    internal static CallScope StartServerCall(RpcMethodDescriptor method, long requestId)
        => StartCall(ServerActivitySource, ActivityKind.Server, "server", method, requestId);

    internal static bool ClientCallsEnabled =>
        ClientActivitySource.HasListeners() || CallMetricsEnabled;

    internal static void ConnectionOpened(string side) => RecordDelta(ActiveConnections, 1, side);
    internal static void ConnectionClosed(string side) => RecordDelta(ActiveConnections, -1, side);
    internal static void ReconnectAttempt() => Record(Reconnects, 1, "client");
    internal static void RecordSentBytes(long bytes) => RecordPositive(SentBytes, bytes);
    internal static void RecordReceivedBytes(long bytes) => RecordPositive(ReceivedBytes, bytes);
    internal static void AddSendQueueBytes(long bytes) => RecordDelta(SendQueueBytes, bytes);
    internal static void AddPendingRequests(long count) => RecordDelta(PendingRequests, count, "client");
    internal static void AddActiveStreams(long count) => RecordDelta(ActiveStreams, count);
    internal static void RecordProtocolFailure(string side) => Record(ProtocolFailures, 1, side);
    internal static void RecordAuthenticationFailure(string side) => Record(AuthenticationFailures, 1, side);
    internal static void RecordResourceExhausted(string side) => Record(ResourceExhausted, 1, side);
    internal static void RecordAbandonedCall(string side) => Record(AbandonedCalls, 1, side);
    internal static void RecordForcedStopCalls(long count) => RecordPositive(ForcedStopCalls, count);

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
}
