namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeFairLifecycleTests
{
    private const int LargePayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainPublishedWorkAcrossConnectionQueues()
    {
        PersistentDecodeReviewService.Reset();
        var provider = new BlockingLifecycleCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await LifecycleHarness.CreateAsync(provider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "single fair decode worker started");

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var a1 = serviceA.MeasureAsync(CreateLargePayload(0x61), CancellationToken.None).AsTask();
        await provider.WaitForStartedCountAsync(1);

        var a2 = serviceA.MeasureAsync(CreateLargePayload(0x62), CancellationToken.None).AsTask();
        var b1 = serviceB.MeasureAsync(CreateLargePayload(0x63), CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 2 &&
                  harness.DecodeQueueReservations == 2 &&
                  harness.DecodeScheduledConnectionCount == 2 &&
                  harness.ActiveDecodes == 1,
            "two connection queues published before graceful stop");

        var stopTask = harness.BeginStopServer(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !harness.DecodeAccepting, "graceful stop sealed decode publication");
        Ensure(!stopTask.IsCompleted,
            "graceful stop must remain joined to running and queued fair-scheduled decode work");
        Ensure(provider.CancellationCount == 0,
            "graceful stop must not force-cancel the running provider before its timeout");

        provider.ReleaseAll();
        await Task.WhenAll(a1, a2, b1).WaitAsync(TimeSpan.FromSeconds(10));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        Ensure(provider.StartedCount == 3,
            "all work published across both connection queues before drain must receive worker service");
        Ensure(provider.CancellationCount == 0,
            "successful graceful fair-scheduler drain must not cancel providers");
        await AssertResourcesReleasedAsync(harness, "multi-connection graceful stop");
    }

    [Test]
    [NotInParallel]
    public async Task ForceStopShouldCancelRunningWorkAndRemoveAllConnectionQueues()
    {
        PersistentDecodeReviewService.Reset();
        var provider = new BlockingLifecycleCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await LifecycleHarness.CreateAsync(provider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "single fair decode worker started");

        var serviceA = harness.ClientA.Get<IPersistentDecodeReviewService>();
        var serviceB = harness.ClientB.Get<IPersistentDecodeReviewService>();
        var a1 = serviceA.MeasureAsync(CreateLargePayload(0x71), CancellationToken.None).AsTask();
        await provider.WaitForStartedCountAsync(1);

        var a2 = serviceA.MeasureAsync(CreateLargePayload(0x72), CancellationToken.None).AsTask();
        var b1 = serviceB.MeasureAsync(CreateLargePayload(0x73), CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == 2 &&
                  harness.DecodeQueueReservations == 2 &&
                  harness.DecodeScheduledConnectionCount == 2 &&
                  harness.ActiveDecodes == 1,
            "two connection queues published before force stop");

        var stopTask = harness.BeginStopServer(TimeSpan.Zero);
        await provider.WaitForCancellationCountAsync(1);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            ObserveExpectedTerminationAsync(a1),
            ObserveExpectedTerminationAsync(a2),
            ObserveExpectedTerminationAsync(b1));

        Ensure(provider.StartedCount == 1,
            "force stop should remove queued fair-scheduler work before another provider start");
        Ensure(provider.CancellationCount == 1,
            "force stop must cancel the running provider exactly once");
        Ensure(PersistentDecodeReviewService.CancellableInvocations == 0,
            "force stop before decode completion must prevent service activation");
        await AssertResourcesReleasedAsync(harness, "multi-connection force stop");
    }

    private static byte[] CreateLargePayload(byte value)
        => Enumerable.Repeat(value, LargePayloadBytes).ToArray();

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

    private static async Task AssertResourcesReleasedAsync(LifecycleHarness harness, string scenario)
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

    private sealed class LifecycleHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _disposed;

        private LifecycleHarness(
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
        internal bool DecodeAccepting => ReadDiagnosticProperty<bool>("DecodeAcceptingForDiagnostics");

        internal static async Task<LifecycleHarness> CreateAsync(ISharpLinkCompressionProvider serverProvider)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 16;
                    options.FlowControl.MaxConcurrentCallsPerServer = 32;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 32L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 32L * 1024 * 1024;
                    options.Compression.Providers.Add(serverProvider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCts.Token);

            var clientA = CreateClient(port);
            var clientB = CreateClient(port);
            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            return new LifecycleHarness(serverCts, serverTask, server, clientA, clientB);
        }

        internal Task BeginStopServer(TimeSpan gracefulTimeout)
            => _server.StopAsync(gracefulTimeout).AsTask();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await StopClientAsync(ClientA);
                await StopClientAsync(ClientB);
            }
            finally
            {
                await _serverCts.CancelAsync();
                await _server.StopAsync(TimeSpan.Zero);
                await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
                _serverCts.Dispose();
            }
        }

        private static ISharpLinkClient CreateClient(int port)
            => SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()))
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

    private sealed class BlockingLifecycleCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new();
        private int _startedCount;
        private int _cancellationCount;

        public string WireProfile => inner.WireProfile;

        internal int StartedCount => Volatile.Read(ref _startedCount);
        internal int CancellationCount => Volatile.Read(ref _cancellationCount);

        internal void ReleaseAll() => _release.Set();

        internal Task WaitForStartedCountAsync(int expected)
            => WaitForCounterAsync(() => StartedCount, expected, "lifecycle provider starts");

        internal Task WaitForCancellationCountAsync(int expected)
            => WaitForCounterAsync(() => CancellationCount, expected, "lifecycle provider cancellations");

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
            Interlocked.Increment(ref _startedCount);
            try
            {
                _release.Wait(cancellationToken);
                return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }
    }

    private static async Task WaitForCounterAsync(Func<int> read, int expected, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (read() < expected)
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario} did not reach {expected}");
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
