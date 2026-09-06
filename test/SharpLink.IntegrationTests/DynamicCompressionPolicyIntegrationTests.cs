namespace SharpLink.IntegrationTests;

public sealed class DynamicCompressionPolicyIntegrationTests
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
    [Arguments("fixed")]
    [Arguments("static")]
    [Arguments("dynamic")]
    [NotInParallel]
    public async Task PreferenceUpdateInHandshakeReadyGapShouldConvergeAfterPublication(string topology)
    {
        var servers = new List<CompressionServerScope>();
        try
        {
            servers.Add(await CompressionServerScope.StartAsync());
            if (topology == "static")
                servers.Add(await CompressionServerScope.StartAsync());

            var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
            var allHooksEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHooks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expectedHooks = topology == "static" ? 2 : 1;
            var enteredHooks = 0;

            ValueTask BeforeReadyPublication(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref enteredHooks) == expectedHooks)
                    allHooksEntered.TrySetResult();
                return new ValueTask(releaseHooks.Task.WaitAsync(cancellationToken));
            }

            var builder = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5))
                .UseRuntime(options => options.Compression.Providers.Add(clientProvider))
                .UseRequestCompressionPolicy(EnabledPolicy)
                .UseBeforeReadyPublicationTestHook(BeforeReadyPublication);
            ConfigureTopology(builder, topology, servers);

            await using var client = builder.Build();
            var connect = client.ConnectAsync().AsTask();
            await allHooksEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await client.SetResponseCompressionPreferenceAsync(false).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            releaseHooks.TrySetResult();
            await connect.WaitAsync(TimeSpan.FromSeconds(5));

            var payload = Enumerable.Repeat((byte)0x2a, 8192).ToArray();
            var response = await client.Get<ICompressionService>().EchoBytesAsync(payload);
            Ensure(response.SequenceEqual(payload), $"{topology}: response payload");
            Ensure(servers.All(static server => server.Provider.CompressCount == 0),
                $"{topology}: a preference published in the handshake/Ready gap must disable every newly Ready server response");
            Ensure(clientProvider.DecompressCount == 0,
                $"{topology}: client must not receive a compressed response after gap reconciliation");
        }
        finally
        {
            for (var index = servers.Count - 1; index >= 0; index--)
                await servers[index].DisposeAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task PublicDynamicPoliciesShouldApplyDirectionallyAcrossAllRpcShapes()
    {
        await using var server = await CompressionServerScope.StartAsync();
        var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), server.Port)
            .UseRuntime(options => options.Compression.Providers.Add(clientProvider))
            .UseRequestCompressionPolicy(EnabledPolicy)
            .Build();
        await client.ConnectAsync();
        var service = client.Get<ICompressionService>();

        await AssertAllRpcShapesAsync(service, clientProvider, server.Provider,
            expectRequestCompression: true, expectResponseCompression: true, "initial enabled");

        client.UpdateRequestCompressionPolicy(DisabledPolicy);
        await AssertAllRpcShapesAsync(service, clientProvider, server.Provider,
            expectRequestCompression: false, expectResponseCompression: true, "request disabled");

        client.UpdateRequestCompressionPolicy(EnabledPolicy);
        await client.SetResponseCompressionPreferenceAsync(false);
        await AssertAllRpcShapesAsync(service, clientProvider, server.Provider,
            expectRequestCompression: true, expectResponseCompression: false, "remote response preference disabled");

        await client.SetResponseCompressionPreferenceAsync(true);
        server.Server.UpdateResponseCompressionPolicy(DisabledPolicy);
        await AssertAllRpcShapesAsync(service, clientProvider, server.Provider,
            expectRequestCompression: true, expectResponseCompression: false, "server response policy disabled");

        server.Server.UpdateResponseCompressionPolicy(EnabledPolicy);
        await AssertAllRpcShapesAsync(service, clientProvider, server.Provider,
            expectRequestCompression: true, expectResponseCompression: true, "re-enabled");
    }

    [Test]
    [NotInParallel]
    public async Task PublicPreferenceUpdateShouldConvergeAcrossFixedReadyCohort()
    {
        var servers = new List<CompressionServerScope>
        {
            await CompressionServerScope.StartAsync(),
            await CompressionServerScope.StartAsync()
        };
        try
        {
            var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
            await using var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseRuntime(options => options.Compression.Providers.Add(clientProvider))
                .UseRequestCompressionPolicy(EnabledPolicy)
                .UseEndpoints(
                    [Endpoint("first", servers[0].Port), Endpoint("second", servers[1].Port)],
                    SharpLinkTransportFactories.Sockets())
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = 2;
                    options.MaxConnections = 2;
                    options.MaxConnectionsPerEndpoint = 1;
                })
                .UseEndpointSelector(new AlternatingSelector())
                .Build();
            await client.ConnectAsync();
            await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));

            await client.SetResponseCompressionPreferenceAsync(false).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var payload = Enumerable.Repeat((byte)0x33, 8192).ToArray();
            var service = client.Get<ICompressionService>();
            Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload), "first cohort response");
            Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload), "second cohort response");

            Ensure(servers.All(static item => item.Provider.DecompressCount > 0),
                "both ready cohort sessions must carry a request");
            Ensure(servers.All(static item => item.Provider.CompressCount == 0),
                "both ready cohort sessions must publish the disabled response preference before the API completes");
        }
        finally
        {
            for (var index = servers.Count - 1; index >= 0; index--)
                await servers[index].DisposeAsync();
        }
    }

    private static void ConfigureTopology(
        SharpClientBuilder builder,
        string topology,
        IReadOnlyList<CompressionServerScope> servers)
    {
        switch (topology)
        {
            case "fixed":
                builder.UseTcp(IPAddress.Loopback.ToString(), servers[0].Port);
                return;
            case "static":
                builder.UseEndpoints(
                        [Endpoint("first", servers[0].Port), Endpoint("second", servers[1].Port)],
                        SharpLinkTransportFactories.Sockets())
                    .UseCluster(options =>
                    {
                        options.MinReadyEndpoints = 2;
                        options.MaxConnections = 2;
                        options.MaxConnectionsPerEndpoint = 1;
                    });
                return;
            case "dynamic":
                builder.UseEndpointResolver(
                    new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", servers[0].Port)])),
                    SharpLinkTransportFactories.Sockets());
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(topology), topology, null);
        }
    }

    private static async Task AssertAllRpcShapesAsync(
        ICompressionService service,
        CountingCompressionProvider clientProvider,
        CountingCompressionProvider serverProvider,
        bool expectRequestCompression,
        bool expectResponseCompression,
        string phase)
    {
        var payload = Enumerable.Repeat((byte)0x2a, 8192).ToArray();

        var before = Snapshot(clientProvider, serverProvider);
        Ensure((await service.EchoBytesAsync(payload)).SequenceEqual(payload), $"{phase}: unary response");
        AssertRequestDelta(before, clientProvider, serverProvider, expectRequestCompression, $"{phase}: unary request");
        AssertResponseDelta(before, clientProvider, serverProvider, expectResponseCompression, $"{phase}: unary response");

        before = Snapshot(clientProvider, serverProvider);
        var upload = await service.UploadBytesAsync(ToAsyncEnumerable([payload, payload], CancellationToken.None));
        Ensure(upload == payload.Length * 2, $"{phase}: client-stream result");
        AssertRequestDelta(before, clientProvider, serverProvider, expectRequestCompression, $"{phase}: client streaming");

        before = Snapshot(clientProvider, serverProvider);
        var download = await CollectAsync(service.DownloadBytesAsync(2, payload.Length), CancellationToken.None);
        Ensure(download.Count == 2 && download.All(item => item.SequenceEqual(payload)), $"{phase}: server-stream result");
        AssertResponseDelta(before, clientProvider, serverProvider, expectResponseCompression, $"{phase}: server streaming");

        before = Snapshot(clientProvider, serverProvider);
        var duplex = await CollectAsync(
            service.DuplexBytesAsync(ToAsyncEnumerable([payload, payload], CancellationToken.None)),
            CancellationToken.None);
        Ensure(duplex.Count == 2 && duplex.All(item => item.SequenceEqual(payload)), $"{phase}: duplex result");
        AssertRequestDelta(before, clientProvider, serverProvider, expectRequestCompression, $"{phase}: duplex request");
        AssertResponseDelta(before, clientProvider, serverProvider, expectResponseCompression, $"{phase}: duplex response");
    }

    private static ProviderSnapshot Snapshot(
        CountingCompressionProvider clientProvider,
        CountingCompressionProvider serverProvider)
        => new(
            clientProvider.CompressCount,
            clientProvider.DecompressCount,
            serverProvider.CompressCount,
            serverProvider.DecompressCount);

    private static void AssertRequestDelta(
        ProviderSnapshot before,
        CountingCompressionProvider clientProvider,
        CountingCompressionProvider serverProvider,
        bool expected,
        string name)
    {
        Ensure((clientProvider.CompressCount > before.ClientCompress) == expected,
            $"{name}: client compression expected={expected}");
        Ensure((serverProvider.DecompressCount > before.ServerDecompress) == expected,
            $"{name}: server decompression expected={expected}");
    }

    private static void AssertResponseDelta(
        ProviderSnapshot before,
        CountingCompressionProvider clientProvider,
        CountingCompressionProvider serverProvider,
        bool expected,
        string name)
    {
        Ensure((serverProvider.CompressCount > before.ServerCompress) == expected,
            $"{name}: server compression expected={expected}");
        Ensure((clientProvider.DecompressCount > before.ClientDecompress) == expected,
            $"{name}: client decompression expected={expected}");
    }

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
    };

    private static async IAsyncEnumerable<byte[]> ToAsyncEnumerable(
        IEnumerable<byte[]> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<T>> CollectAsync<T>(
        IAsyncEnumerable<T> values,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await foreach (var value in values.WithCancellation(cancellationToken))
            result.Add(value);
        return result;
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {description}.");
    }

    private readonly record struct ProviderSnapshot(
        int ClientCompress,
        int ClientDecompress,
        int ServerCompress,
        int ServerDecompress);

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

        public static Task<CompressionServerScope> StartAsync()
        {
            var provider = new CountingCompressionProvider(new TestCompressionProvider());
            var builder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5))
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseResponseCompressionPolicy(EnabledPolicy)
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new CompressionServerScope(builder.Build(), provider, port));
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

    private sealed class FixedResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlternatingSelector : ISharpLinkEndpointSelector
    {
        private int _next = -1;

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var attempt = 0; attempt < context.Count; attempt++)
            {
                var candidate = (Interlocked.Increment(ref _next) & int.MaxValue) % context.Count;
                if ((context.ExcludedMask & (1UL << candidate)) == 0)
                    return candidate;
            }
            return -1;
        }
    }
}
