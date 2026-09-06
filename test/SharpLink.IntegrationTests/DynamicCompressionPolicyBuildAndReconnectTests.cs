namespace SharpLink.IntegrationTests;

public sealed class DynamicCompressionPolicyBuildAndReconnectTests
{
    private static readonly SharpLinkCompressionSendPolicy EnabledPolicy = new()
    {
        MinimumPayloadBytes = 0,
        MinimumSavingsBytes = 0,
        MinimumSavingsRatio = 0
    };

    private static readonly SharpLinkCompressionSendPolicy DisabledPolicy = new()
    {
        Enabled = false,
        MinimumPayloadBytes = 0,
        MinimumSavingsBytes = 0,
        MinimumSavingsRatio = 0
    };

    [Test]
    [NotInParallel]
    public async Task BuildTimeDisabledDirectionalPoliciesShouldNegotiateAndEnableWithoutReconnect()
    {
        await using var server = await CompressionServerScope.StartAsync(responsePolicy: DisabledPolicy);
        var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5))
            .UseTcp(IPAddress.Loopback.ToString(), server.Port)
            .UseRuntime(options => options.Compression.Providers.Add(clientProvider))
            .UseRequestCompressionPolicy(DisabledPolicy)
            .Build();
        await client.ConnectAsync();

        var service = client.Get<ICompressionService>();
        var payload = Enumerable.Repeat((byte)0x4a, 8192).ToArray();
        Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload),
            "build-time disabled policies must preserve the raw RPC result");
        Ensure(clientProvider.CompressCount == 0 && server.Provider.DecompressCount == 0,
            "build-time disabled Request policy must bypass the negotiated provider");
        Ensure(server.Provider.CompressCount == 0 && clientProvider.DecompressCount == 0,
            "build-time disabled Response policy must bypass the negotiated provider");

        client.UpdateRequestCompressionPolicy(EnabledPolicy);
        server.Server.UpdateResponseCompressionPolicy(EnabledPolicy);

        Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload),
            "runtime-enabled policies must preserve the RPC result on the same connection");
        Ensure(clientProvider.CompressCount > 0 && server.Provider.DecompressCount > 0,
            "the same session must already have negotiated compression for Request runtime enablement");
        Ensure(server.Provider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "the same session must already have negotiated compression for Response runtime enablement");
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 1,
            "runtime policy enablement must not reconnect the fixed client");
    }

    [Test]
    [NotInParallel]
    public async Task ReconnectedSessionShouldInheritLatestResponsePreferenceWithoutJoiningPriorCohort()
    {
        var first = await CompressionServerScope.StartAsync();
        CompressionServerScope? replacement = null;
        try
        {
            var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
            await using var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1))
                .UseTcp(IPAddress.Loopback.ToString(), first.Port)
                .UseRuntime(options => options.Compression.Providers.Add(clientProvider))
                .UseRequestCompressionPolicy(EnabledPolicy)
                .Build();
            await client.ConnectAsync();

            await client.SetResponseCompressionPreferenceAsync(false).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var payload = Enumerable.Repeat((byte)0x35, 8192).ToArray();
            var service = client.Get<ICompressionService>();
            Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload),
                "the original session must remain usable after preference convergence");
            Ensure(first.Provider.DecompressCount > 0 && first.Provider.CompressCount == 0,
                "the original session must negotiate compression while respecting disabled responses");

            var port = first.Port;
            await first.DisposeAsync();
            await WaitUntilAsync(
                () => ((SharpLinkClient)client).ReadyConnectionCount == 0,
                TimeSpan.FromSeconds(3),
                "the stopped server connection must leave the Ready set before replacement");

            replacement = await CompressionServerScope.StartAsync(port);
            await WaitUntilAsync(
                () => ((SharpLinkClient)client).ReadyConnectionCount == 1,
                TimeSpan.FromSeconds(5),
                "the fixed client must reconnect to the replacement server");

            var clientDecompressBefore = clientProvider.DecompressCount;
            Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload),
                "the reconnected session must serve the RPC without another preference Set");
            Ensure(replacement.Provider.DecompressCount > 0,
                "the replacement session must negotiate compression and decode the enabled Request direction");
            Ensure(replacement.Provider.CompressCount == 0 && clientProvider.DecompressCount == clientDecompressBefore,
                "the replacement session handshake must inherit the latest disabled response preference");
        }
        finally
        {
            if (replacement is not null)
                await replacement.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (!condition())
                await Task.Delay(10, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Assertion failed: {description}.");
        }
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {description}.");
    }

    private sealed class CountingCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _compressCount;
        private int _decompressCount;

        public string WireProfile => inner.WireProfile;
        public int CompressCount => Volatile.Read(ref _compressCount);
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            return inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class CompressionServerScope : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _runTask;
        private int _disposed;

        private CompressionServerScope(ISharpLinkServer server, CountingCompressionProvider provider, int port)
        {
            Server = server;
            Provider = provider;
            Port = port;
            _runTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(_cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);
        }

        public ISharpLinkServer Server { get; }
        public CountingCompressionProvider Provider { get; }
        public int Port { get; }

        public static Task<CompressionServerScope> StartAsync(
            int port = 0,
            SharpLinkCompressionSendPolicy? responsePolicy = null)
        {
            var provider = new CountingCompressionProvider(new TestCompressionProvider());
            var builder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5))
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseResponseCompressionPolicy(responsePolicy ?? EnabledPolicy)
                .UseTcp(port, IPAddress.Loopback.ToString());
            var boundPort = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new CompressionServerScope(builder.Build(), provider, boundPort));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await Server.StopAsync(TimeSpan.Zero);
            await _cancellation.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(1000, CancellationToken.None));
            await Server.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
