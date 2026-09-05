using System.Diagnostics.Metrics;
using System.Reflection;

namespace SharpLink.IntegrationTests;

internal sealed class LifecycleMetricProbe : IDisposable
{
    internal const string ActiveConnections = "sharplink.connections.active";
    internal const string ActiveCalls = "sharplink.calls.active";
    internal const string PendingRequests = "sharplink.requests.pending";
    internal const string ActiveStreams = "sharplink.streams.active";
    internal const string AdmissionPermits = "sharplink.admission.permits.active";
    internal const string AdmissionQueuedCalls = "sharplink.admission.calls.queued";

    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _instrumentNames;
    private readonly MeterListener _listener;
    private TaskCompletionSource _changed = NewSignal();

    internal LifecycleMetricProbe(params string[] instrumentNames)
    {
        _instrumentNames = new HashSet<string>(instrumentNames, StringComparer.Ordinal);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SharpLinkTelemetry.Meter.Name &&
                    _instrumentNames.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _ = tags;
            _ = state;
            lock (_gate)
            {
                _values.TryGetValue(instrument.Name, out var current);
                _values[instrument.Name] = checked(current + measurement);
                var changed = _changed;
                _changed = NewSignal();
                changed.TrySetResult();
            }
        });
        _listener.Start();
    }

    internal long GetValue(string instrumentName)
    {
        lock (_gate)
            return _values.GetValueOrDefault(instrumentName);
    }

    internal async Task WaitForValueAsync(string instrumentName, long expected, string scenario)
        => await WaitForAsync(
            instrumentName,
            value => value == expected,
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scenario);

    internal async Task WaitForAtLeastAsync(string instrumentName, long minimum, string scenario)
        => await WaitForAsync(
            instrumentName,
            value => value >= minimum,
            $">= {minimum}",
            scenario);

    private async Task WaitForAsync(
        string instrumentName,
        Func<long, bool> condition,
        string expected,
        string scenario)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (condition(_values.GetValueOrDefault(instrumentName)))
                    return;
                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(ObservationTimeout);
            }
            catch (TimeoutException)
            {
                long actual;
                lock (_gate)
                    actual = _values.GetValueOrDefault(instrumentName);
                throw new TimeoutException(
                    $"{scenario}: metric {instrumentName} did not reach {expected}; " +
                    $"actual {actual}.");
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal readonly record struct ServerLifecycleResourceSnapshot(
    int ActiveCalls,
    int Connections,
    int RetiredConnections,
    int AdmissionPermits,
    int AdmissionQueuedCalls,
    long AdmissionQueuedBytes);

internal static class ServerLifecycleResourceInspector
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static ServerLifecycleResourceSnapshot Capture(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var sharpServer = (SharpLinkServer)server;
        var serverType = server.GetType();
        var admission = serverType.GetField("_admissionController", InstanceFlags)?.GetValue(server);
        return new ServerLifecycleResourceSnapshot(
            ServerCallAdmissionDiagnostics.ActiveCallCount(server),
            ServerRegistryTestAccessor.ActiveConnectionCount(sharpServer),
            ServerRegistryTestAccessor.RetiredConnectionCount(sharpServer),
            ReadIntProperty(admission, "ActivePermits"),
            ReadIntProperty(admission, "QueuedCalls"),
            ReadLongProperty(admission, "QueuedBytes"));
    }

    private static int ReadIntProperty(object? value, string name)
        => value is null
            ? 0
            : (int)(value.GetType().GetProperty(name, InstanceFlags)?.GetValue(value) ??
                throw new InvalidOperationException($"Admission property '{name}' was not found."));

    private static long ReadLongProperty(object? value, string name)
        => value is null
            ? 0
            : (long)(value.GetType().GetProperty(name, InstanceFlags)?.GetValue(value) ??
                throw new InvalidOperationException($"Admission property '{name}' was not found."));
}
