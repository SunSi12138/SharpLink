namespace SharpLink.IntegrationTests;

public class CompressionDecodeCancellationTests
{
    [Test]
    [NotInParallel]
    public async Task CompressedUnaryDecodeShouldObserveDeadlineCancellation()
    {
        DeadlineCompressionProbeService.Reset();
        var serverProvider = new WaitForCancellationDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        var requestTimeout = TimeSpan.FromMilliseconds(100);
        await using var harness = await DecodeCancellationHarness.CreateAsync(
            serverProvider,
            requestTimeout);
        var payload = Enumerable.Repeat((byte)0x46, 32 * 1024).ToArray();
        var call = harness.Client.Get<IDeadlineCompressionProbeService>()
            .EchoAsync(payload)
            .AsTask();

        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await EnsureDeadlineExceededAsync(call, "compressed unary decode cancellation");
        await WaitUntilAsync(() => harness.ActiveCalls == 0, "cancelled unary decode call release");

        Ensure(DeadlineCompressionProbeService.UnaryInvocations == 0,
            "deadline-cancelled compressed unary request must not execute the service");
    }

    [Test]
    [NotInParallel]
    public async Task CompressedOneWayDecodeShouldObserveDeadlineCancellation()
    {
        DeadlineCompressionProbeService.Reset();
        var serverProvider = new WaitForCancellationDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        var requestTimeout = TimeSpan.FromMilliseconds(100);
        await using var harness = await DecodeCancellationHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x47, 32 * 1024).ToArray();

        await harness.Client.Get<IDeadlineCompressionProbeService>()
            .NotifyAsync(payload, new SharpLinkCallOptions { Timeout = requestTimeout })
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => harness.ActiveCalls == 0, "cancelled one-way decode call release");

        Ensure(DeadlineCompressionProbeService.OneWayInvocations == 0,
            "deadline-cancelled compressed one-way request must not execute the service");
    }

    private static async Task EnsureDeadlineExceededAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded,
                $"{scenario} should return DeadlineExceeded, actual {exception.Code}");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not fail fast");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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

    private sealed class WaitForCancellationDecompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string WireProfile => inner.WireProfile;

        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;

        public Task WaitForCancellationAsync() => _cancellationObserved.Task;

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
            _decompressionStarted.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            _cancellationObserved.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            throw new Exception("assert failed: cancelled decompression should not continue");
        }
    }

    private sealed class DecodeCancellationHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        public ISharpLinkClient Client { get; }

        public int ActiveCalls
        {
            get
            {
                var reflectionField = _server.GetType().GetField(
                    "_globalActiveCalls",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("cannot find active call counter");
                return (int)reflectionField.GetValue(_server)!;
            }
        }

        private DecodeCancellationHarness(
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

        public static async Task<DecodeCancellationHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            TimeSpan? requestTimeout = null)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                    options.FlowControl.MaxConcurrentCallsPerServer = 1;
                    options.Compression.Providers.Add(serverProvider);
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

            var clientBuilder = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()));
            if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);
            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new DecodeCancellationHarness(serverCts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            await _serverCts.CancelAsync();
            await _server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
