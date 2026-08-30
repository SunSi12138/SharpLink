using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeFairnessTests
{
    private const int LargePayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task NoisyConnectionShouldNotTakeTwoQueuedTurnsBeforePeerConnection()
    {
        PersistentDecodeReviewService.Reset();
        var coordinator = new FairnessCoordinator(ignoreFirstCancellation: false);
        await using var harness = await FairHarness.CreateAsync(coordinator);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "single fair decode worker started");

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var payloadA = Enumerable.Repeat((byte)0x41, LargePayloadBytes).ToArray();
        var payloadB = Enumerable.Repeat((byte)0x42, LargePayloadBytes).ToArray();
        using var cancellation = new CancellationTokenSource();

        var a1 = serviceA.MeasureAsync(payloadA, cancellation.Token).AsTask();
        await coordinator.WaitForStartedCountAsync(1);
        Ensure(coordinator.StartOrder[0] == "A", "connection A must own the intentionally blocked first turn");

        var a2 = serviceA.MeasureAsync(payloadA, cancellation.Token).AsTask();
        var a3 = serviceA.MeasureAsync(payloadA, cancellation.Token).AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 2 &&
                  harness.DecodeQueueReservations == 2 &&
                  harness.DecodeScheduledConnectionCount == 1,
            "connection A backlog entered its scheduler queue before B publication");

        var b1 = serviceB.MeasureAsync(payloadB, cancellation.Token).AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 3 &&
                  harness.DecodeQueueReservations == 3 &&
                  harness.DecodeScheduledConnectionCount == 2,
            "A backlog and B request entered two fair scheduler queues");

        coordinator.ReleaseFirst();
        await coordinator.WaitForStartedCountAsync(4);
        await Task.WhenAll(a1, a2, a3, b1).WaitAsync(TimeSpan.FromSeconds(10));

        var order = coordinator.StartOrder;
        Ensure(order.Count >= 4, "all four provider starts must be recorded");
        Ensure(order[0] == "A" && order[1] == "A" && order[2] == "B",
            $"round-robin service must give B the next connection turn; observed {string.Join(',', order)}");
        await AssertResourcesReleasedAsync(harness, "two-connection fair drain");
    }

    [Test]
    [NotInParallel]
    public async Task ClosingConnectionShouldRemoveItsQueuedTurnBeforeBlockedProviderReturns()
    {
        PersistentDecodeReviewService.Reset();
        var coordinator = new FairnessCoordinator(ignoreFirstCancellation: true);
        await using var harness = await FairHarness.CreateAsync(coordinator);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "single fair decode worker started");

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var payloadA = Enumerable.Repeat((byte)0x51, LargePayloadBytes).ToArray();
        var payloadB = Enumerable.Repeat((byte)0x52, LargePayloadBytes).ToArray();
        using var cancellation = new CancellationTokenSource();

        var a1 = serviceA.MeasureAsync(payloadA, cancellation.Token).AsTask();
        await coordinator.WaitForStartedCountAsync(1);
        var a2 = serviceA.MeasureAsync(payloadA, cancellation.Token).AsTask();
        var b1 = serviceB.MeasureAsync(payloadB, cancellation.Token).AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 2 &&
                  harness.DecodeQueueReservations == 2 &&
                  harness.DecodeScheduledConnectionCount == 2,
            "two connections queued behind the blocked provider");

        var stopA = harness.ClientA.StopAsync().AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 1 &&
                  harness.DecodeQueueReservations == 1 &&
                  harness.DecodeScheduledConnectionCount == 1,
            "closed connection queued ownership removed before worker availability");
        Ensure(coordinator.StartOrder.Count == 1,
            "connection-close cleanup must not require the blocked provider to return first");

        coordinator.ReleaseFirst();
        await coordinator.WaitForStartedCountAsync(2);
        await b1.WaitAsync(TimeSpan.FromSeconds(10));
        await ObserveExpectedTerminationAsync(a1);
        await ObserveExpectedTerminationAsync(a2);
        await stopA.WaitAsync(TimeSpan.FromSeconds(5));

        var order = coordinator.StartOrder;
        Ensure(order.Count >= 2 && order[1] == "B",
            $"remaining connection must receive the next worker turn after A closes; observed {string.Join(',', order)}");
        await AssertResourcesReleasedAsync(harness, "connection-close fair cleanup");
    }

    private static async Task ObserveExpectedTerminationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.Cancelled or
                SharpLinkErrorCode.ConnectionClosed or
                SharpLinkErrorCode.Unavailable)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task AssertResourcesReleasedAsync(FairHarness harness, string scenario)
    {
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 &&
                  harness.ActiveDecodes == 0 &&
                  harness.RetainedCompressedBytes == 0 &&
                  harness.DecodedBytesInFlight == 0 &&
                  harness.DecodeQueueDepth == 0 &&
                  harness.DecodeQueueReservations == 0 &&
                  harness.DecodeScheduledConnectionCount == 0,
            $"{scenario} resource release");
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
            throw new Exception($"assert failed: {scenario} was not observed");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class FairHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _stopped;

        private FairHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient clientA,
            ISharpLinkClient clientB)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            ClientA = clientA;
            ClientB = clientB;
        }

        internal ISharpLinkClient ClientA { get; }
        internal ISharpLinkClient ClientB { get; }

        internal int ActiveCalls => ReadField<int>("_globalActiveCalls");
        internal int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        internal long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        internal long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");
        internal int DecodeWorkerCount => ReadDiagnosticProperty<int>("DecodeWorkerCountForDiagnostics");
        internal int DecodeQueueDepth => ReadDiagnosticProperty<int>("DecodeQueueDepthForDiagnostics");
        internal int DecodeQueueReservations =>
            ReadDiagnosticProperty<int>("DecodeQueueReservationsForDiagnostics");
        internal int DecodeScheduledConnectionCount =>
            ReadDiagnosticProperty<int>("DecodeScheduledConnectionCountForDiagnostics");

        internal static async Task<FairHarness> CreateAsync(FairnessCoordinator coordinator)
        {
            var serverCts = new CancellationTokenSource();
            var serverProviderA = new TaggedCompressionProvider(
                "review-fair-a",
                "A",
                SharpLinkCompressionProviders.CreateBrotli(),
                coordinator);
            var serverProviderB = new TaggedCompressionProvider(
                "review-fair-b",
                "B",
                SharpLinkCompressionProviders.CreateBrotli(),
                coordinator);
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 16;
                    options.FlowControl.MaxConcurrentCallsPerServer = 32;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 32L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 32L * 1024 * 1024;
                    options.Compression.Providers.Add(serverProviderA);
                    options.Compression.Providers.Add(serverProviderB);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCts.Token);

            var clientA = CreateClient(port, "review-fair-a", "A");
            var clientB = CreateClient(port, "review-fair-b", "B");
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new FairHarness(serverCts, serverTask, server, clientA, clientB);
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            try
            {
                await ClientA.StopAsync();
                await ClientB.StopAsync();
            }
            finally
            {
                await _serverCts.CancelAsync();
                await _server.StopAsync(TimeSpan.Zero);
                await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
                _serverCts.Dispose();
            }
        }

        private static ISharpLinkClient CreateClient(int port, string wireProfile, string tag)
            => SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    new TaggedCompressionProvider(
                        wireProfile,
                        tag,
                        SharpLinkCompressionProviders.CreateBrotli(),
                        coordinator: null)))
                .Build();

        private T ReadField<T>(string name)
        {
            var field = _server.GetType().GetField(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find server field {name}");
            return (T)field.GetValue(_server)!;
        }

        private T ReadDiagnosticProperty<T>(string name)
        {
            var property = _server.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find server diagnostic property {name}");
            return (T)property.GetValue(_server)!;
        }
    }

    private sealed class FairnessCoordinator(bool ignoreFirstCancellation)
    {
        private readonly ManualResetEventSlim _releaseFirst = new();
        private readonly ConcurrentQueue<string> _startOrder = new();
        private int _startedCount;

        internal IReadOnlyList<string> StartOrder => _startOrder.ToArray();

        internal void ReleaseFirst() => _releaseFirst.Set();

        internal async Task WaitForStartedCountAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (Volatile.Read(ref _startedCount) < expected)
                    await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Exception($"assert failed: provider starts did not reach {expected}");
            }
        }

        internal CancellationToken RecordStartAndBlockFirst(string tag, CancellationToken cancellationToken)
        {
            _startOrder.Enqueue(tag);
            var ordinal = Interlocked.Increment(ref _startedCount);
            if (ordinal != 1)
                return cancellationToken;

            if (ignoreFirstCancellation)
            {
                _releaseFirst.Wait(CancellationToken.None);
                return CancellationToken.None;
            }

            _releaseFirst.Wait(cancellationToken);
            return cancellationToken;
        }
    }

    private sealed class TaggedCompressionProvider(
        string wireProfile,
        string tag,
        ISharpLinkCompressionProvider inner,
        FairnessCoordinator? coordinator) : ISharpLinkCompressionProvider
    {
        public string WireProfile => wireProfile;

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            var effectiveCancellation = coordinator?.RecordStartAndBlockFirst(tag, cancellationToken)
                ?? cancellationToken;
            return inner.Decompress(input, output, maxOutputBytes, effectiveCancellation);
        }
    }

    private static Task RunServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }, CancellationToken.None);
}
