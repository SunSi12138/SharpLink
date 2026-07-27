using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace SharpLink.IntegrationTests;

public class TelemetryIntegrationTests
{
    [Test]
    public async Task EarlyClientStreamDisposalShouldNotReportSuccessfulCompletion()
    {
        var activities = new ConcurrentQueue<ActivitySnapshot>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(new ActivitySnapshot(
                activity.Source.Name,
                activity.Status,
                activity.TagObjects.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value?.ToString())))
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var harness = await TelemetryHarness.CreateAsync();
        await using (var stream = harness.Client.Get<IInterceptorTestService>()
                         .FailStreamAsync()
                         .GetAsyncEnumerator())
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1,
                "early-disposal telemetry stream item");
        }

        var streamingActivity = activities.SingleOrDefault(static activity =>
            activity.Source == "SharpLink.Client" &&
            activity.Tags.TryGetValue("rpc.sharplink.method_kind", out var kind) &&
            kind == nameof(RpcMethodKind.ServerStreaming));
        Ensure(streamingActivity.Status == ActivityStatusCode.Error,
            "consumer-abandoned Client stream activity must not report success");
        Ensure(streamingActivity.Tags.TryGetValue("error.type", out var errorType) &&
               errorType == typeof(OperationCanceledException).FullName,
            "consumer-abandoned Client stream activity should retain cancellation identity");
    }

    [Test]
    public async Task RpcShouldPublishActivitiesAndRequiredMetrics()
    {
        var activities = new ConcurrentQueue<ActivitySnapshot>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is "SharpLink.Client" or "SharpLink.Server",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(new ActivitySnapshot(
                activity.Source.Name,
                activity.Status,
                activity.TagObjects.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value?.ToString())))
        };
        ActivitySource.AddActivityListener(activityListener);

        var instrumentNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != "SharpLink")
                return;
            instrumentNames.TryAdd(instrument.Name, 0);
            listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Enqueue(new MetricMeasurement(instrument.Name, measurement)));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Enqueue(new MetricMeasurement(instrument.Name, measurement)));
        _ = SharpLinkTelemetry.Meter;
        meterListener.Start();

        await using (var harness = await TelemetryHarness.CreateAsync())
        {
            var service = harness.Client.Get<IInterceptorTestService>();
            Ensure(await service.DescribeNumberAsync(4) == 5, "telemetry unary result");

            var stream = service.FailStreamAsync().GetAsyncEnumerator();
            try
            {
                Ensure(await stream.MoveNextAsync() && stream.Current == 1, "telemetry stream item");
                await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        var requiredInstruments = new[]
        {
            "sharplink.connections.active",
            "sharplink.connections.reconnects",
            "sharplink.calls.started",
            "sharplink.calls.completed",
            "sharplink.calls.failed",
            "sharplink.calls.active",
            "sharplink.calls.duration",
            "sharplink.transport.bytes.sent",
            "sharplink.transport.bytes.received",
            "sharplink.send.queue.bytes",
            "sharplink.requests.pending",
            "sharplink.streams.active",
            "sharplink.protocol.failures",
            "sharplink.authentication.failures",
            "sharplink.resource_exhausted",
            "sharplink.calls.abandoned",
            "sharplink.responses.late_dropped"
        };
        foreach (var name in requiredInstruments)
            Ensure(instrumentNames.ContainsKey(name), $"published metric {name}");

        Ensure(activities.Any(static activity =>
            activity.Source == "SharpLink.Client" && activity.Status == ActivityStatusCode.Ok),
            "client success activity");
        Ensure(activities.Any(static activity =>
            activity.Source == "SharpLink.Server" && activity.Status == ActivityStatusCode.Ok),
            "server success activity");
        Ensure(activities.Any(static activity =>
            activity.Status == ActivityStatusCode.Error),
            "failed stream activity");
        Ensure(activities.Any(static activity =>
            activity.Tags.ContainsKey("rpc.sharplink.contract_id") &&
            activity.Tags.ContainsKey("rpc.sharplink.method_id")),
            "activity method tags");

        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.connections.active" && value.Value > 0),
            "active connection increment");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.connections.active" && value.Value < 0),
            "active connection decrement");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.calls.started" && value.Value > 0),
            "calls started");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.calls.failed" && value.Value > 0),
            "calls failed");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.calls.duration" && value.Value >= 0),
            "call duration");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.transport.bytes.sent" && value.Value > 0),
            "sent bytes");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.transport.bytes.received" && value.Value > 0),
            "received bytes");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.send.queue.bytes" && value.Value > 0),
            "send queue increment");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.send.queue.bytes" && value.Value < 0),
            "send queue decrement");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.requests.pending" && value.Value > 0),
            "pending request increment");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.requests.pending" && value.Value < 0),
            "pending request decrement");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.streams.active" && value.Value > 0),
            "active stream increment");
        Ensure(measurements.Any(static value =>
            value.Name == "sharplink.streams.active" && value.Value < 0),
            "active stream decrement");
    }

    private static async Task CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("assert failed: expected SharpLinkException");
        }
        catch (SharpLinkException)
        {
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private readonly record struct ActivitySnapshot(
        string Source,
        ActivityStatusCode Status,
        IReadOnlyDictionary<string, string?> Tags);

    private readonly record struct MetricMeasurement(string Name, double Value);

    private sealed class TelemetryHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        public ISharpLinkClient Client { get; }

        private TelemetryHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            Client = client;
        }

        public static async Task<TelemetryHarness> CreateAsync()
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .Build();
            await client.ConnectAsync(cts.Token);
            return new TelemetryHarness(cts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await _server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
