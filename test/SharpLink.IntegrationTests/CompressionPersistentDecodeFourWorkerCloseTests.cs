using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeFourWorkerCloseTests
{
    private const int PayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task ConnectionCloseShouldRemoveQueuedTurnWhileFourWorkersStayBusy()
    {
        PersistentDecodeReviewService.Reset();
        var coordinator = new Coordinator();
        await using var harness = await Harness.CreateAsync(coordinator);
        await WaitUntilAsync(() => harness.WorkerCount == 4, "four decode workers started");

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var payloadA = Enumerable.Repeat((byte)0x61, PayloadBytes).ToArray();
        var payloadB = Enumerable.Repeat((byte)0x62, PayloadBytes).ToArray();

        var runningA = Enumerable.Range(0, 4)
            .Select(_ => serviceA.MeasureAsync(payloadA, CancellationToken.None).AsTask())
            .ToArray();
        await coordinator.WaitForStartsAsync(4);
        await WaitUntilAsync(
            () => harness.ActiveDecodes == 4 && harness.QueueDepth == 0,
            "four A providers occupied all workers");

        var queuedA = serviceA.MeasureAsync(payloadA, CancellationToken.None).AsTask();
        var queuedB = serviceB.MeasureAsync(payloadB, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => harness.QueueDepth == 2 && harness.QueueReservations == 2 &&
                  harness.ScheduledConnections == 2 && harness.ActiveDecodes == 4,
            "A and B queued behind occupied workers");

        var stopA = harness.ClientA.StopAsync().AsTask();
        await WaitUntilAsync(
            () => harness.QueueDepth == 1 && harness.QueueReservations == 1 &&
                  harness.ScheduledConnections == 1 && harness.ActiveDecodes == 4,
            "closed A queue removed before worker availability");
        Ensure(coordinator.StartOrder.Count == 4,
            "queued close cleanup must not require a worker to return");

        coordinator.ReleaseA();
        await coordinator.WaitForStartsAsync(5);
        await queuedB.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(runningA.Select(ObserveTerminationAsync));
        await ObserveTerminationAsync(queuedA);
        await stopA.WaitAsync(TimeSpan.FromSeconds(5));

        Ensure(coordinator.StartOrder[4] == "B",
            $"B must receive the first post-close start; observed {string.Join(',', coordinator.StartOrder)}");
        await WaitUntilAsync(
            () => harness.ActiveDecodes == 0 && harness.QueueDepth == 0 &&
                  harness.QueueReservations == 0 && harness.ScheduledConnections == 0,
            "four-worker close resources released");
    }

    private static async Task ObserveTerminationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.Cancelled or SharpLinkErrorCode.ConnectionClosed or
                SharpLinkErrorCode.Unavailable)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        private Harness(CancellationTokenSource serverCts, Task serverTask, ISharpLinkServer server,
            ISharpLinkClient clientA, ISharpLinkClient clientB)
            => (_serverCts, _serverTask, _server, ClientA, ClientB) =
                (serverCts, serverTask, server, clientA, clientB);

        internal ISharpLinkClient ClientA { get; }
        internal ISharpLinkClient ClientB { get; }
        internal int ActiveDecodes => Read<int>("ActiveDecodeCountForDiagnostics");
        internal int WorkerCount => Read<int>("DecodeWorkerCountForDiagnostics");
        internal int QueueDepth => Read<int>("DecodeQueueDepthForDiagnostics");
        internal int QueueReservations => Read<int>("DecodeQueueReservationsForDiagnostics");
        internal int ScheduledConnections => Read<int>("DecodeScheduledConnectionCountForDiagnostics");

        internal static async Task<Harness> CreateAsync(Coordinator coordinator)
        {
            var cts = new CancellationTokenSource();
            var builder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 16;
                    options.FlowControl.MaxConcurrentCallsPerServer = 32;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 4;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 64L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 64L * 1024 * 1024;
                    options.Compression.Providers.Add(new Provider("review-four-a", "A", coordinator));
                    options.Compression.Providers.Add(new Provider("review-four-b", "B", coordinator));
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            var server = builder.Build();
            var serverTask = RunServerAsync(server, cts.Token);
            var clientA = CreateClient(port, "review-four-a", "A");
            var clientB = CreateClient(port, "review-four-b", "B");
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new Harness(cts, serverTask, server, clientA, clientB);
        }

        public async ValueTask DisposeAsync()
        {
            await StopClientAsync(ClientA);
            await StopClientAsync(ClientB);
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private T Read<T>(string name)
            => (T)(_server.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find {name}")).GetValue(_server)!;

        private static ISharpLinkClient CreateClient(int port, string profile, string tag)
            => SharpClientBuilder.Create().UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(new Provider(profile, tag, null)))
                .Build();

        private static async Task StopClientAsync(ISharpLinkClient client)
        {
            try { await client.StopAsync(); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SharpLinkException) { }
        }
    }

    private sealed class Coordinator
    {
        private readonly ManualResetEventSlim _releaseA = new();
        private readonly ConcurrentQueue<string> _order = new();
        private int _starts;
        internal IReadOnlyList<string> StartOrder => _order.ToArray();
        internal void ReleaseA() => _releaseA.Set();
        internal async Task WaitForStartsAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref _starts) < expected)
                await Task.Delay(10, timeout.Token);
        }
        internal CancellationToken Record(string tag, CancellationToken token)
        {
            _order.Enqueue(tag);
            Interlocked.Increment(ref _starts);
            if (tag == "A")
            {
                _releaseA.Wait(CancellationToken.None);
                return CancellationToken.None;
            }
            return token;
        }
    }

    private sealed class Provider(string profile, string tag, Coordinator? coordinator) : ISharpLinkCompressionProvider
    {
        private readonly ISharpLinkCompressionProvider _inner = SharpLinkCompressionProviders.CreateBrotli();
        public string WireProfile => profile;
        public SharpLinkCompressionResult Compress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
            => _inner.Compress(input, output, maxOutputBytes, cancellationToken);
        public SharpLinkCompressionResult Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
            => _inner.Decompress(input, output, maxOutputBytes,
                coordinator?.Record(tag, cancellationToken) ?? cancellationToken);
    }

    private static Task RunServerAsync(ISharpLinkServer server, CancellationToken token)
        => Task.Run(async () =>
        {
            try { await server.RunAsync(token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or SocketException) { }
        }, CancellationToken.None);
}
