using System.Diagnostics.Metrics;

namespace SharpLink.IntegrationTests;

[NotInParallel]
public sealed class TelemetryObserverIsolationIntegrationTests
{
    [Test]
    public async Task ThrowingCompletionMeterListenerShouldNotReplaceResultOrPoisonSameSession()
    {
        await using var harness = await TelemetryObserverIsolationHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();
        var initialSession = ExtractSessionId(await service.DescribeAsync(1));

        using (var listener = new ThrowingMeterScope("sharplink.calls.completed", "client"))
        {
            var result = await service.DescribeNumberAsync(41).ConfigureAwait(false);
            Ensure(result == 42,
                "client completion metric observer failure must not replace the successful RPC result");
            Ensure(listener.ThrowCount == 1,
                "client completion metric observer callback should be exercised exactly once");
        }

        var reusedSession = ExtractSessionId(await service.DescribeAsync(2));
        Ensure(string.Equals(initialSession, reusedSession, StringComparison.Ordinal),
            "completion metric observer failure must not poison the existing connection");
        Ensure(await service.DescribeNumberAsync(99).ConfigureAwait(false) == 100,
            "healthy RPC should succeed immediately after completion metric observer failure");
    }

    [Test]
    public async Task ThrowingFailedMeterListenerShouldNotReplaceAuthoritativeErrorOrPoisonSameSession()
    {
        await using var harness = await TelemetryObserverIsolationHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();
        var initialSession = ExtractSessionId(await service.DescribeAsync(5));

        using (var listener = new ThrowingMeterScope("sharplink.calls.failed", "client"))
        {
            var failure = await CaptureSharpLinkException(service.FailAsync().AsTask()).ConfigureAwait(false);
            Ensure(failure.Code == SharpLinkErrorCode.Internal,
                "client failed-call metric observer must preserve the authoritative RPC error");
            Ensure(listener.ThrowCount == 1,
                "client failed-call metric observer callback should be exercised exactly once");
        }

        var reusedSession = ExtractSessionId(await service.DescribeAsync(6));
        Ensure(string.Equals(initialSession, reusedSession, StringComparison.Ordinal),
            "failed-call metric observer failure must not poison the existing connection");
        Ensure(await service.DescribeNumberAsync(299).ConfigureAwait(false) == 300,
            "healthy RPC should succeed immediately after failed-call metric observer failure");
    }

    [Test]
    public async Task ThrowingActivityStoppedCallbackShouldNotReplaceResultOrPoisonSameSession()
    {
        await using var harness = await TelemetryObserverIsolationHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();
        var initialSession = ExtractSessionId(await service.DescribeAsync(3));

        using (var listener = new ThrowingActivityStoppedScope())
        {
            var result = await service.DescribeNumberAsync(41).ConfigureAwait(false);
            Ensure(result == 42,
                "ActivityStopped observer failure must not replace the successful RPC result");
            Ensure(listener.ThrowCount == 1,
                "ActivityStopped observer callback should be exercised exactly once");
        }

        var reusedSession = ExtractSessionId(await service.DescribeAsync(4));
        Ensure(string.Equals(initialSession, reusedSession, StringComparison.Ordinal),
            "ActivityStopped observer failure must not poison the existing connection");
        Ensure(await service.DescribeNumberAsync(199).ConfigureAwait(false) == 200,
            "healthy RPC should succeed immediately after ActivityStopped observer failure");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("assert failed: expected SharpLinkException");
    }

    private static string ExtractSessionId(string description)
    {
        var separator = description.LastIndexOf('|');
        Ensure(separator >= 0 && separator + 1 < description.Length,
            $"description should contain a session id: {description}");
        return description[(separator + 1)..];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class ThrowingMeterScope : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly string _instrumentName;
        private readonly string? _side;
        private int _remaining = 1;
        private int _throwCount;

        internal ThrowingMeterScope(string instrumentName, string? side = null)
        {
            _instrumentName = instrumentName;
            _side = side;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter) &&
                    string.Equals(instrument.Name, _instrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                if (!string.Equals(instrument.Name, _instrumentName, StringComparison.Ordinal) ||
                    (_side is not null && !HasSide(tags, _side)) ||
                    Interlocked.Exchange(ref _remaining, 0) == 0)
                {
                    return;
                }

                Interlocked.Increment(ref _throwCount);
                throw new InvalidOperationException("injected completion MeterListener failure");
            });
            _listener.Start();
        }

        internal int ThrowCount => Volatile.Read(ref _throwCount);

        public void Dispose() => _listener.Dispose();

        private static bool HasSide(ReadOnlySpan<KeyValuePair<string, object?>> tags, string side)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && string.Equals(tag.Value as string, side, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private sealed class ThrowingActivityStoppedScope : IDisposable
    {
        private readonly ActivityListener _listener;
        private int _remaining = 1;
        private int _throwCount;

        internal ThrowingActivityStoppedScope()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source =>
                    ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _ =>
                {
                    if (Interlocked.Exchange(ref _remaining, 0) != 0)
                    {
                        Interlocked.Increment(ref _throwCount);
                        throw new InvalidOperationException("injected ActivityStopped failure");
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal int ThrowCount => Volatile.Read(ref _throwCount);

        public void Dispose() => _listener.Dispose();
    }

    private sealed class TelemetryObserverIsolationHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        private TelemetryObserverIsolationHarness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _server = server;
            Client = client;
        }

        internal ISharpLinkClient Client { get; }

        internal static async Task<TelemetryObserverIsolationHarness> CreateAsync()
        {
            var cancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
                .Build();
            try
            {
                await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
                return new TelemetryObserverIsolationHarness(
                    cancellation,
                    serverTask,
                    server,
                    client);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
                cancellation.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await _serverCancellation.CancelAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            _serverCancellation.Dispose();
        }
    }
}
