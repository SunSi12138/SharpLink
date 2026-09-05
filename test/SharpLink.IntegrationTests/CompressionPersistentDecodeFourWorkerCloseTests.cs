using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeWorkerSaturationTests
{
    private const int PayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task ConnectionCloseShouldRemoveQueuedTurnWhileAllWorkersStayBusy()
    {
        PersistentDecodeReviewService.Reset();
        var coordinator = new Coordinator();
        await using var harness = await Harness.CreateAsync(coordinator);
        var workerCount = GetPortableWorkerCount(harness);

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var payloadA = Enumerable.Repeat((byte)0x61, PayloadBytes).ToArray();
        var payloadB = Enumerable.Repeat((byte)0x62, PayloadBytes).ToArray();

        using var callCancellation = new CancellationTokenSource();

        var runningA = Enumerable.Range(0, workerCount)
            .Select(_ => serviceA.MeasureAsync(payloadA, callCancellation.Token).AsTask())
            .ToArray();
        await coordinator.WaitForStartsAsync(workerCount);
        await WaitUntilAsync(
            () => harness.ActiveDecodes == workerCount && harness.QueueDepth == 0,
            $"all {workerCount} A providers occupied the available workers");

        var queuedA = serviceA.MeasureAsync(payloadA, callCancellation.Token).AsTask();
        var queuedB = serviceB.MeasureAsync(payloadB, callCancellation.Token).AsTask();
        await WaitUntilAsync(
            () => harness.QueueDepth == 2 && harness.QueueReservations == 2 &&
                  harness.ScheduledConnections == 2 && harness.ActiveDecodes == workerCount,
            "A and B queued behind occupied workers");

        var stopA = harness.ClientA.StopAsync().AsTask();
        await WaitUntilAsync(
            () => harness.QueueDepth == 1 && harness.QueueReservations == 1 &&
                  harness.ScheduledConnections == 1 && harness.ActiveDecodes == workerCount,
            "closed A queue removed before worker availability");
        Ensure(coordinator.StartOrder.Count == workerCount,
            "queued close cleanup must not require a worker to return");

        coordinator.ReleaseA();
        await coordinator.WaitForStartsAsync(workerCount + 1);
        await queuedB.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(runningA.Select(ObserveTerminationAsync));
        await ObserveTerminationAsync(queuedA);
        await stopA.WaitAsync(TimeSpan.FromSeconds(5));

        Ensure(coordinator.StartOrder[workerCount] == "B",
            $"B must receive the first post-close start; observed {string.Join(',', coordinator.StartOrder)}");
        await AssertSchedulerReleasedAsync(harness, "connection-close worker saturation");
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldRemoveQueuedTurnWithoutPerturbingPeerWhileAllWorkersStayBusy()
    {
        PersistentDecodeReviewService.Reset();
        var coordinator = new Coordinator();
        await using var harness = await Harness.CreateAsync(coordinator);
        var workerCount = GetPortableWorkerCount(harness);

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var payloadA = Enumerable.Repeat((byte)0x71, PayloadBytes).ToArray();
        var payloadB = Enumerable.Repeat((byte)0x72, PayloadBytes).ToArray();

        using var callCancellation = new CancellationTokenSource();

        var runningA = Enumerable.Range(0, workerCount)
            .Select(_ => serviceA.MeasureAsync(payloadA, callCancellation.Token).AsTask())
            .ToArray();
        await coordinator.WaitForStartsAsync(workerCount);
        await WaitUntilAsync(
            () => harness.ActiveDecodes == workerCount && harness.QueueDepth == 0,
            $"all {workerCount} A providers occupied the available workers before remote Cancel");

        using var queuedCancellation = new CancellationTokenSource();
        var queuedA = serviceA.MeasureAsync(payloadA, queuedCancellation.Token).AsTask();
        var queuedB = serviceB.MeasureAsync(payloadB, callCancellation.Token).AsTask();
        await WaitUntilAsync(
            () => harness.QueueDepth == 2 && harness.QueueReservations == 2 &&
                  harness.ScheduledConnections == 2 && harness.ActiveDecodes == workerCount,
            "A and B queued behind occupied workers before remote Cancel");

        queuedCancellation.Cancel();
        await WaitUntilAsync(
            () => harness.QueueDepth == 1 && harness.QueueReservations == 1 &&
                  harness.ScheduledConnections == 1 && harness.ActiveDecodes == workerCount,
            "remote Cancel removed only A queued ownership before worker availability");
        Ensure(coordinator.StartOrder.Count == workerCount,
            "remote Cancel cleanup must not consume a worker or start B early");

        coordinator.ReleaseA();
        await coordinator.WaitForStartsAsync(workerCount + 1);
        await queuedB.WaitAsync(TimeSpan.FromSeconds(10));
        await ObserveCancellationAsync(queuedA);
        await Task.WhenAll(runningA.Select(ObserveTerminationAsync));

        Ensure(coordinator.StartOrder[workerCount] == "B",
            $"B must receive the first post-cancel start; observed {string.Join(',', coordinator.StartOrder)}");
        await AssertSchedulerReleasedAsync(harness, "remote Cancel worker saturation");
    }

    private static int GetPortableWorkerCount(Harness harness)
    {
        var workerCount = harness.WorkerCount;
        Ensure(workerCount is >= 1 and <= 4,
            $"decode worker count must stay within the production 1..4 clamp; observed {workerCount}");
        return workerCount;
    }

    private static async Task AssertSchedulerReleasedAsync(Harness harness, string scenario)
    {
        await WaitUntilAsync(
            () => harness.ActiveDecodes == 0 && harness.QueueDepth == 0 &&
                  harness.QueueReservations == 0 && harness.ScheduledConnections == 0,
            $"{scenario} resources released");
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            throw new Exception("assert failed: remotely cancelled queued call should not complete successfully");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Cancelled)
        {
        }
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
        private readonly Coordinator _coordinator;

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient clientA,
            ISharpLinkClient clientB,
            Coordinator coordinator)
            => (_serverCts, _serverTask, _server, ClientA, ClientB, _coordinator) =
                (serverCts, serverTask, server, clientA, clientB, coordinator);

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
                    options.Compression.Providers.Add(new Provider("review-saturation-a", "A", coordinator));
                    options.Compression.Providers.Add(new Provider("review-saturation-b", "B", coordinator));
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            var server = builder.Build();
            var serverTask = RunServerAsync(server, cts.Token);
            var clientA = CreateClient(port, "review-saturation-a", "A");
            var clientB = CreateClient(port, "review-saturation-b", "B");
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new Harness(cts, serverTask, server, clientA, clientB, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            _coordinator.ReleaseA();
            await StopClientAsync(ClientA);
            await StopClientAsync(ClientB);
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private T Read<T>(string name)
            => (T)(_server.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find {name}")).GetValue(_server)!;

        private static ISharpLinkClient CreateClient(int port, string profile, string tag)
            => SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(new Provider(profile, tag, null)))
                .Build();

        private static async Task StopClientAsync(ISharpLinkClient client)
        {
            try
            {
                await client.StopAsync();
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or ObjectDisposedException or SharpLinkException)
            {
            }
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
            try
            {
                while (Volatile.Read(ref _starts) < expected)
                    await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Exception($"assert failed: provider starts did not reach {expected}");
            }
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

    private sealed class Provider(string profile, string tag, Coordinator? coordinator)
        : ISharpLinkCompressionProvider
    {
        private readonly ISharpLinkCompressionProvider _inner =
            SharpLinkCompressionProviders.CreateBrotli();

        public string WireProfile => profile;

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => _inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => _inner.Decompress(
                input,
                output,
                maxOutputBytes,
                coordinator?.Record(tag, cancellationToken) ?? cancellationToken);
    }

    private static Task RunServerAsync(ISharpLinkServer server, CancellationToken token)
        => Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
        }, CancellationToken.None);
}
