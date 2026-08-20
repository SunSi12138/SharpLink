namespace SharpLink.IntegrationTests;

public class CompressionDispatchFailureTests
{
    [Test]
    [NotInParallel]
    public async Task CompressedUnaryProviderFailureShouldReleaseDispatchResources()
    {
        var serverProvider = new FailOnceAfterWriteDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await DispatchFailureHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x45, 32 * 1024).ToArray();

        await EnsureDataLossAsync(
            harness.Client.Get<ICompressionService>().EchoBytesAsync(payload).AsTask(),
            "compressed unary provider failure");
        await WaitUntilAsync(() => harness.ActiveCalls == 0, "failed compressed call release");

        Ensure(serverProvider.CapturedOutputReturned,
            "decoded request writer must be returned after decompression failure");

        var response = await harness.Client.Get<ICompressionService>()
            .EchoBytesAsync(payload)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(response.SequenceEqual(payload),
            "connection should remain usable after compressed request decode failure");
        await WaitUntilAsync(() => harness.ActiveCalls == 0, "recovery call release");
    }

    private static async Task EnsureDataLossAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.DataLoss,
                $"{scenario} should return DataLoss, actual {exception.Code}");
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

    private sealed class FailOnceAfterWriteDecompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private IBufferWriter<byte>? _capturedOutput;
        private int _failureInjected;

        public string WireProfile => inner.WireProfile;

        public bool CapturedOutputReturned
        {
            get
            {
                if (Volatile.Read(ref _capturedOutput) is not IRpcByteBufferWriter writer)
                    return false;
                try
                {
                    _ = writer.WrittenCount;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

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
            if (Interlocked.Exchange(ref _failureInjected, 1) == 0)
            {
                Volatile.Write(ref _capturedOutput, output);
                var span = output.GetSpan(1);
                span[0] = 0x7F;
                output.Advance(1);
                throw new InvalidDataException("Synthetic decompression failure after output allocation.");
            }

            return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class DispatchFailureHarness : IAsyncDisposable
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

        private DispatchFailureHarness(
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

        public static async Task<DispatchFailureHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider)
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

            var client = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()))
                .Build();
            await client.ConnectAsync();

            return new DispatchFailureHarness(serverCts, serverTask, server, client);
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
