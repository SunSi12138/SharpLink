namespace SharpLink.IntegrationTests;

public class CompressionDecodeDeadlineAdmissionIndependenceTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompressedUnaryDeadlineShouldCancelProviderRegardlessOfAdvancedAdmission(
        bool useAdvancedAdmission)
    {
        DeadlineCompressionProbeService.Reset();
        var serverProvider = new DeadlineBlockingCompressionProvider(
            new TestCompressionProvider());
        await using var harness = await DeadlineHarness.CreateAsync(
            serverProvider,
            useAdvancedAdmission,
            TimeSpan.FromMilliseconds(100));
        var payload = Enumerable.Repeat((byte)0x51, 32 * 1024).ToArray();
        var call = harness.Client.Get<IDeadlineCompressionProbeService>()
            .EchoAsync(payload)
            .AsTask();

        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await EnsureDeadlineExceededAsync(call, "deadline-aware provider cancellation");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 &&
                  harness.ActiveDecodes == 0 &&
                  harness.RetainedCompressedBytes == 0 &&
                  harness.DecodedBytesInFlight == 0,
            "deadline decode ownership release");

        Ensure(DeadlineCompressionProbeService.UnaryInvocations == 0,
            "deadline-cancelled compressed request must not execute the service");
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

    private sealed class DeadlineBlockingCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string WireProfile => inner.WireProfile;

        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;

        public Task WaitForCancellationAsync() => _cancellationObserved.Task;

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.TryCompress(input, output, maxOutputBytes, cancellationToken);

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _decompressionStarted.TrySetResult();
            try
            {
                using var blocked = new ManualResetEventSlim(initialState: false);
                blocked.Wait(cancellationToken);
                throw new InvalidOperationException("The deadline probe must be cancelled before decompression resumes.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class DeadlineHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        private DeadlineHarness(
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

        public ISharpLinkClient Client { get; }
        public int ActiveCalls => ServerCallAdmissionDiagnostics.ActiveCallCount(_server);
        public int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        public long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        public long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");

        internal static async Task<DeadlineHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            bool useAdvancedAdmission,
            TimeSpan requestTimeout)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                    options.FlowControl.MaxConcurrentCallsPerServer = 1;
                    options.Compression.Providers.Add(serverProvider);
                });
            if (useAdvancedAdmission)
            {
                serverBuilder.UseAdmissionControl(options =>
                    options.Global.UseConcurrency(8));
            }

            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
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
                .UseRequestTimeout(requestTimeout)
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    new TestCompressionProvider()))
                .Build();
            await client.ConnectAsync();
            return new DeadlineHarness(serverCts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
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
