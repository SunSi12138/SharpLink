namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodePreActivationRaceTests
{
    private const int LargePayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task RemoteCancelBeforeActivationShouldWinEvenWhenProviderReturnsSuccessfully()
    {
        PersistentDecodeReviewService.Reset();
        var provider = new SuccessfulAfterCancelCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RaceHarness.CreateAsync(provider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "persistent decode worker started");

        using var cancellation = new CancellationTokenSource();
        var payload = Enumerable.Repeat((byte)0x5a, LargePayloadBytes).ToArray();
        var call = harness.Client.Get<IPersistentDecodeReviewService>()
            .MeasureAsync(payload, cancellation.Token)
            .AsTask();

        try
        {
            await provider.WaitForStartedCountAsync(1);
            await cancellation.CancelAsync();
            await provider.WaitForCancellationObservedCountAsync(1);

            Ensure(harness.DecodeStartedWorkCount == 1,
                "the cancellation race probe must execute through persistent D");
            Ensure(PersistentDecodeReviewService.CancellableInvocations == 0,
                "the handler must not start while provider decode remains blocked");

            provider.ReleaseAll();
            await EnsureCancelledAsync(call, "remote cancel before activation");
            await WaitUntilAsync(
                () => harness.ActiveCalls == 0 &&
                      harness.ActiveDecodes == 0 &&
                      harness.RetainedCompressedBytes == 0 &&
                      harness.DecodedBytesInFlight == 0 &&
                      harness.DecodeQueueDepth == 0 &&
                      harness.DecodeQueueReservations == 0,
                "pre-activation cancellation resource release");

            Ensure(provider.CompletedCount == 1,
                "provider must return successfully after server cancellation was already observed");
            Ensure(PersistentDecodeReviewService.CancellableInvocations == 0,
                "cancellation that wins before activation must prevent user dispatch");
        }
        finally
        {
            provider.ReleaseAll();
        }
    }

    private static async Task EnsureCancelledAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should cancel");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Cancelled)
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
            throw new Exception($"assert failed: {scenario} was not observed");
        }
    }

    private static async Task WaitForCounterAsync(
        Func<int> read,
        int expected,
        string scenario)
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

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class SuccessfulAfterCancelCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new();
        private int _startedCount;
        private int _cancellationObservedCount;
        private int _completedCount;

        public string WireProfile => inner.WireProfile;
        internal int CompletedCount => Volatile.Read(ref _completedCount);

        internal void ReleaseAll() => _release.Set();

        internal Task WaitForStartedCountAsync(int count)
            => WaitForCounterAsync(
                () => Volatile.Read(ref _startedCount),
                count,
                "race provider starts");

        internal Task WaitForCancellationObservedCountAsync(int count)
            => WaitForCounterAsync(
                () => Volatile.Read(ref _cancellationObservedCount),
                count,
                "race provider server cancellation observations");

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
            using var registration = cancellationToken.UnsafeRegister(
                static state => Interlocked.Increment(
                    ref ((SuccessfulAfterCancelCompressionProvider)state!)._cancellationObservedCount),
                this);

            // Deliberately ignore cancellation while blocked, then decode with CancellationToken.None.
            // This proves the framework's pre-activation terminal check rather than relying on a
            // cooperative provider to throw OperationCanceledException.
            _release.Wait();
            var result = inner.Decompress(input, output, maxOutputBytes, CancellationToken.None);
            Interlocked.Increment(ref _completedCount);
            return result;
        }
    }

    private sealed class RaceHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _stopped;

        private RaceHarness(
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

        internal ISharpLinkClient Client { get; }
        internal int ActiveCalls => ServerCallAdmissionDiagnostics.ActiveCallCount(_server);
        internal int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        internal long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        internal long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");
        internal int DecodeWorkerCount => ReadDiagnosticProperty<int>("DecodeWorkerCountForDiagnostics");
        internal int DecodeQueueDepth => ReadDiagnosticProperty<int>("DecodeQueueDepthForDiagnostics");
        internal int DecodeQueueReservations =>
            ReadDiagnosticProperty<int>("DecodeQueueReservationsForDiagnostics");
        internal int DecodeStartedWorkCount =>
            ReadDiagnosticProperty<int>("DecodeStartedWorkCountForDiagnostics");

        internal static async Task<RaceHarness> CreateAsync(ISharpLinkCompressionProvider provider)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 8;
                    options.FlowControl.MaxConcurrentCallsPerServer = 8;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 16L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 16L * 1024 * 1024;
                    options.Compression.Providers.Add(provider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(serverCts.Token);
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

            var client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()))
                .Build();
            await client.ConnectAsync();
            return new RaceHarness(serverCts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            try
            {
                await Client.StopAsync();
            }
            catch (Exception)
            {
            }
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
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
}
