namespace SharpLink.IntegrationTests;

public class IntegrationBehaviorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BasicRpcAndStreamingShouldWork(bool useSharedMemory)
    {
        static IRpcCodec? Resolver(Type type)
        {
            if (type == typeof(GeneratedEnvelope) ||
                type == typeof(GeneratedAddress) ||
                type == typeof(List<string>))
            {
                throw new Exception($"Generated Codec unexpectedly fell through to resolver: {type}.");
            }
            return MemoryPackCodec.Resolver?.Invoke(type);
        }

        await using var harness = await TestHarness.CreateAsync(
            codecResolver: Resolver,
            useSharedMemory: useSharedMemory);
        var svc = harness.Client.Get<ITestService>();

        var add = await svc.AddAsync(10, 20);
        Ensure(add == 30, "AddAsync");

        var echo = await svc.EchoAsync(new Person { Name = "s", Age = 1, Tags = ["x"] });
        Ensure(echo is { Name: "s-r", Age: 2 }, "EchoAsync");

        var generated = await svc.EchoGeneratedAsync(new GeneratedEnvelope(
            "native",
            7,
            new GeneratedAddress("Shanghai"),
            ["rpc", "aot"]));
        Ensure(generated is
        {
            Name: "native-r",
            Age: 8,
            Address.City: "Shanghai",
            Tags.Count: 2
        }, "EchoGeneratedAsync");

        var sum = await svc.UploadAsync(ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None));
        Ensure(sum == 10, "UploadAsync");

        var values = await CollectAsync(svc.DownloadAsync(3), CancellationToken.None);
        Ensure(values.SequenceEqual(["v-0", "v-1", "v-2"]), "DownloadAsync");

        await svc.NotifyAsync("ok");
    }

    [Test]
    public async Task NegotiatedBrotliShouldCompressUnaryRequestAndResponse()
    {
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));

        var source = new Person
        {
            Name = new string('a', 16 * 1024),
            Age = 7,
            Tags = [new string('b', 4096)]
        };
        var response = await harness.Client.Get<ITestService>().EchoAsync(source);

        Ensure(response.Name == source.Name + "-r", "compressed unary response");
        Ensure(clientProvider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "client compression provider should handle both directions");
        Ensure(serverProvider.CompressCount > 0 && serverProvider.DecompressCount > 0,
            "server compression provider should handle both directions");
    }

    [Test]
    public async Task EncodingLevelsMayDifferAcrossOneNegotiatedWireProfile()
    {
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli(
            System.IO.Compression.CompressionLevel.Optimal));
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli(
            System.IO.Compression.CompressionLevel.SmallestSize));
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));

        var payload = Enumerable.Repeat((byte)0x2a, 16 * 1024).ToArray();
        var response = await harness.Client.Get<ICompressionService>().EchoBytesAsync(payload);

        Ensure(response.SequenceEqual(payload), "different local encoding levels");
        Ensure(clientProvider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "client should encode and decode with its local provider configuration");
        Ensure(serverProvider.CompressCount > 0 && serverProvider.DecompressCount > 0,
            "server should encode and decode with its local provider configuration");
    }

    [Test]
    public async Task ServerProviderOrderShouldSelectFirstMutualAlgorithm()
    {
        var clientGzip = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateGzip());
        var clientBrotli = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverBrotli = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverGzip = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateGzip());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(clientGzip);
                options.Compression.Providers.Add(clientBrotli);
            },
            serverRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(serverBrotli);
                options.Compression.Providers.Add(serverGzip);
            });

        var result = await harness.Client.Get<ICompressionService>()
            .EchoBytesAsync(Enumerable.Repeat((byte)3, 4096).ToArray());
        Ensure(result.Length == 4096, "provider preference call");
        Ensure(clientBrotli.CompressCount > 0 && serverBrotli.DecompressCount > 0,
            "server-first mutual provider should be selected");
        Ensure(clientGzip.CompressCount == 0 && serverGzip.DecompressCount == 0,
            "lower-priority provider should remain idle");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task OneSidedOrDisjointCompressionShouldFallBackToRawFrames(bool oneSided)
    {
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateGzip());
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: oneSided
                ? null
                : options => options.Compression.Providers.Add(serverProvider));

        var result = await harness.Client.Get<ITestService>().EchoAsync(new Person
        {
            Name = new string('x', 4096),
            Age = 1,
            Tags = ["fallback"]
        });

        Ensure(result.Age == 2, "fallback unary result");
        Ensure(clientProvider.CompressCount == 0 && clientProvider.DecompressCount == 0,
            "unselected client provider must remain idle");
        Ensure(serverProvider.CompressCount == 0 && serverProvider.DecompressCount == 0,
            "unselected server provider must remain idle");
    }

    [Test]
    public async Task NegotiatedCompressionShouldCoverOneWayAndEveryStreamingShape()
    {
        CompressionService.ResetOneWay();
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateGzip());
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateGzip());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();
        var payload = Enumerable.Repeat((byte)0x2a, 8192).ToArray();

        var unary = await service.EchoBytesAsync(payload);
        Ensure(unary.SequenceEqual(payload), "compressed unary bytes");

        await service.NotifyBytesAsync(payload);
        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == payload.Length,
            "compressed one-way execution");

        var upload = await service.UploadBytesAsync(
            ToAsyncEnumerable([payload, payload, payload], CancellationToken.None));
        Ensure(upload == payload.Length * 3, "compressed client stream");

        var download = await CollectAsync(service.DownloadBytesAsync(3, payload.Length), CancellationToken.None);
        Ensure(download.Count == 3 && download.All(item => item.SequenceEqual(payload)),
            "compressed server stream");

        var duplex = await CollectAsync(
            service.DuplexBytesAsync(ToAsyncEnumerable([payload, payload], CancellationToken.None)),
            CancellationToken.None);
        Ensure(duplex.Count == 2 && duplex.All(item => item.SequenceEqual(payload)),
            "compressed duplex stream");

        Ensure(clientProvider.CompressCount >= 5 && clientProvider.DecompressCount >= 5,
            "client provider should cover streaming frames");
        Ensure(serverProvider.CompressCount >= 5 && serverProvider.DecompressCount >= 5,
            "server provider should cover streaming frames");
    }

    [Test]
    public async Task SmallOrUnprofitablePayloadShouldRemainUncompressed()
    {
        var clientProvider = new CountingCompressionProvider(new NoBenefitCompressionProvider());
        var serverProvider = new CountingCompressionProvider(new NoBenefitCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        var small = Enumerable.Repeat((byte)1, 128).ToArray();
        Ensure((await service.EchoBytesAsync(small)).SequenceEqual(small), "small raw fallback");
        Ensure(clientProvider.CompressCount == 0 && serverProvider.CompressCount == 0,
            "small payload should bypass providers");

        var large = Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray();
        Ensure((await service.EchoBytesAsync(large)).SequenceEqual(large), "unprofitable raw fallback");
        Ensure(clientProvider.CompressCount > 0 && serverProvider.CompressCount > 0,
            "large payload should evaluate provider benefit");
        Ensure(clientProvider.DecompressCount == 0 && serverProvider.DecompressCount == 0,
            "unprofitable candidates must not reach the peer decoder");
    }

    [Test]
    public async Task CompressionProviderFailureShouldFailOneCallAndKeepConnectionHealthy()
    {
        var clientProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "custom compression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 1, 2, 3 })).SequenceEqual(new byte[] { 1, 2, 3 }),
            "connection should remain healthy after local compression failure");
    }

    [Test]
    public async Task DecompressionProviderFailureShouldReturnInternalAndKeepConnectionHealthy()
    {
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "custom decompression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 4, 5, 6 })).SequenceEqual(new byte[] { 4, 5, 6 }),
            "connection should remain healthy after remote decompression failure");
    }

    [Test]
    public async Task ServerCompressionProviderFailureShouldFailUnaryAndKeepConnectionHealthy()
    {
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "server compression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 7, 8, 9 })).SequenceEqual(new byte[] { 7, 8, 9 }),
            "connection should remain healthy after response compression failure");
    }

    [Test]
    public async Task CompressedServerStreamDecodeFailureShouldReleasePendingCall()
    {
        var clientProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            CollectAsync(service.DownloadBytesAsync(100, 4096), CancellationToken.None),
            "compressed server stream decode failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 3, 2, 1 })).SequenceEqual(new byte[] { 3, 2, 1 }),
            "connection should remain healthy after stream decompression failure");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OneByteFlowWindowsShouldResumeBothStreamDirections(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(runtimeConfigure: options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 1;
            options.FlowControl.ConnectionReceiveWindowBytes = 1;
        }, useSharedMemory: useSharedMemory);
        var svc = harness.Client.Get<ITestService>();

        var upload = await svc.UploadAsync(
            ToAsyncEnumerable(Enumerable.Range(1, 64), CancellationToken.None));
        Ensure(upload == 2080, "one-byte client stream flow control");

        var download = await CollectAsync(svc.DownloadAsync(64), CancellationToken.None);
        Ensure(download.Count == 64 && download[63] == "v-63", "one-byte server stream flow control");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectionPoolShouldExpandOnceUnderConcurrentPressure(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 2;
        }, useSharedMemory: useSharedMemory);
        var client = (SharpLinkClient)harness.Client;
        var svc = harness.Client.Get<ITestService>();

        var first = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(20);
        var second = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await WaitUntilAsync(() => client.ReadyConnectionCount == 2);

        Ensure(await first == 21 && await second == 21, "concurrent calls should complete across the pool");
        Ensure(client.ReadyConnectionCount == 2, "pressure should create one bounded expansion connection");
    }

    [Test]
    public async Task TwoClientServerPairsShouldUseIndependentDtoCodecs()
    {
        var firstCodec = new MarkerPersonCodec(0xA1);
        var secondCodec = new MarkerPersonCodec(0xB2);
        IRpcCodec? FirstResolver(Type type) => type == typeof(Person) ? firstCodec : MemoryPackCodec.Resolver?.Invoke(type);
        IRpcCodec? SecondResolver(Type type) => type == typeof(Person) ? secondCodec : MemoryPackCodec.Resolver?.Invoke(type);
        await using var first = await TestHarness.CreateAsync(codecResolver: FirstResolver);
        await using var second = await TestHarness.CreateAsync(codecResolver: SecondResolver);

        var firstResult = await first.Client.Get<ITestService>()
            .EchoAsync(new Person { Name = "first", Age = 1, Tags = ["a"] });
        var secondResult = await second.Client.Get<ITestService>()
            .EchoAsync(new Person { Name = "second", Age = 2, Tags = ["b"] });

        Ensure(firstResult is { Name: "first-r", Age: 2 }, "first context codec");
        Ensure(secondResult is { Name: "second-r", Age: 3 }, "second context codec");
        Ensure(firstCodec.SerializeCount > 0 && firstCodec.DeserializeCount > 0, "first codec should be used");
        Ensure(secondCodec.SerializeCount > 0 && secondCodec.DeserializeCount > 0, "second codec should be used");
    }

    [Test]
    public async Task UserCancellationShouldPropagateOperationCanceledException()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await EnsureThrows<OperationCanceledException>(
            svc.SlowAddAsync(1, 2, cts.Token).AsTask(),
            "SlowAddAsync user cancellation");
    }

    [Test]
    public async Task DefaultRequestTimeoutShouldThrowDeadlineExceeded()
    {
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask(),
            "SlowAddAsync request timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    [NotInParallel]
    public async Task UnaryWithoutTimeoutAttributeShouldUseClientDefaultTimeout()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithoutTimeoutAsync(1, 2).AsTask(),
            "SlowAddWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable result must be suppressed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableFailureAfterTimeoutShouldBeObservedAndSuppressed()
    {
        TestService.ResetNonCancellableFailure();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowThrowWithoutTimeoutAsync().AsTask(),
            "SlowThrowWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableFailureAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable exception must be observed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task DisableRequestTimeoutShouldAllowNonCancellableUnaryToFinish()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(disableRequestTimeout: true);
        var svc = harness.Client.Get<ITestService>();

        Ensure(await svc.SlowAddWithoutTimeoutAsync(20, 22) == 42,
            "disabled default timeout should wait for the service result");
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task NonCancellableOperationCanceledExceptionShouldNotBeMisreportedAsDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        await EnsureThrowsSharpLinkFast(
            harness.Client.Get<ITestService>().ThrowCancellationAsync().AsTask(),
            "non-cancellable service cancellation classification",
            SharpLinkErrorCode.Cancelled);
    }

    [Test]
    public async Task EarlyServerStreamDisposalShouldCancelAndReleaseConnectionState()
    {
        await using var harness = await TestHarness.CreateAsync();
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using var enumerator = service
                .SlowDownloadAsync(1_000, 10, CancellationToken.None)
                .GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");
        }

        await WaitUntilAsync(() =>
            client.PendingCallCount == 0 &&
            client.ActiveClientCallCount == 0 &&
            client.ActiveClientStreamCount == 0);
        Ensure(await service.AddAsync(20, 22) == 42, "connection should remain healthy after early disposal");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableServerStreamEarlyBreakShouldStopFrameworkPump()
    {
        TestService.ResetDownloadDisposed();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        await using (var enumerator = service.DownloadAsync(int.MaxValue).GetAsyncEnumerator())
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");

        await TestService.WaitForDownloadDisposedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await service.AddAsync(20, 22) == 42,
            "framework stream cancellation must leave the connection healthy");
    }

    [Test]
    [NotInParallel]
    public async Task FastEarlyBreakShouldReturnFlowCreditAndNotLeakCompletedSendStates()
    {
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            await using var enumerator = service.DownloadAsync(32).GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(), "fast stream should produce its first item");
        }

        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after 10,000 fast early-break streams");
    }

    [Test]
    public async Task CallOptionsShouldCarryMetadataAndUseEarliestDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "factory-a"));

        var summary = await svc.DescribeCallAsync(
            42,
            new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Deadline = DateTimeOffset.UtcNow.AddSeconds(5),
                Metadata = metadata
            },
            CancellationToken.None);
        Ensure(summary.StartsWith("42:factory-a:deadline", StringComparison.Ordinal), "metadata/deadline call context");

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithOptionsAsync(
                1,
                2,
                new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(100) },
                CancellationToken.None).AsTask(),
            "call options timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    public async Task ServerDisconnectShouldFailFastPendingUnaryAndStream()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
        var streamTask = CollectAsync(svc.SlowDownloadAsync(100, 200, CancellationToken.None), CancellationToken.None);

        await Task.Delay(100);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(unaryTask, "unary fail-fast", SharpLinkErrorCode.ConnectionClosed);
        await EnsureThrowsSharpLinkFast(streamTask, "stream fail-fast", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task ClientDisposeShouldFailFastPendingUnaryAndStream()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
        var streamTask = CollectAsync(svc.SlowDownloadAsync(100, 200, CancellationToken.None), CancellationToken.None);

        await Task.Delay(100);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(unaryTask, "unary fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
        await EnsureThrowsSharpLinkFast(streamTask, "stream fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamingStopRaceShouldReleaseEveryServerInvocation()
    {
        const int callCount = 128;
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 4;
        });
        var service = harness.Client.Get<ITestService>();
        using var producerCancellation = new CancellationTokenSource();
        var calls = new Task<int>[callCount];
        for (var index = 0; index < calls.Length; index++)
        {
            calls[index] = service.UploadAsync(
                YieldOneThenWaitAsync(index, producerCancellation.Token)).AsTask();
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == callCount);
        var stopServer = harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask();
        var stopClient = harness.DisposeClientOnlyAsync().AsTask();
        await producerCancellation.CancelAsync();
        await Task.WhenAll(stopServer, stopClient).WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is OperationCanceledException or SharpLinkException)
            {
            }
        }
        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamProducerFailureShouldTerminateRemoteInvocationAndKeepConnectionHealthy()
    {
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            await EnsureClientStreamProducerFailure(
                service.UploadAsync(YieldThenFailAsync(iteration)).AsTask(),
                "client stream producer failure");
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after client stream producer failures");
    }

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainAcceptedCallAndRejectNewCallsAfterGoAway()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var acceptedCall = svc.SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(50);
        var stopTask = harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(2)).AsTask();
        await WaitUntilAsync(() => harness.Client.State == SharpLinkConnectionState.Draining);

        await EnsureThrowsSharpLinkFast(
            svc.AddAsync(1, 1).AsTask(),
            "new call after GoAway",
            SharpLinkErrorCode.Unavailable);
        Ensure(await acceptedCall == 42, "accepted call should complete during grace period");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task GraceTimeoutShouldCancelRemainingServerCall()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        using var callCts = new CancellationTokenSource();

        var pending = svc.SlowAddAsync(1, 2, callCts.Token).AsTask();
        await Task.Delay(50);
        var started = Stopwatch.GetTimestamp();
        await harness.DisposeServerOnlyAsync(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Ensure(elapsed < TimeSpan.FromSeconds(2), "grace timeout should cancel the server call promptly");
        await EnsureThrowsSharpLinkFast(pending, "call remaining after grace timeout", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    [NotInParallel]
    public async Task ServerConcurrencyExhaustionShouldRejectOverflowAndRecoverWithoutClosingConnection()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var calls = new Task<int>[1025];

        for (var index = 0; index < calls.Length; index++)
            calls[index] = svc.SlowAddAsync(index, 1, CancellationToken.None).AsTask();

        var completed = 0;
        var exhausted = 0;
        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(10));
                completed++;
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                exhausted++;
            }
        }

        Ensure(completed == 1024, "server should admit exactly the per-connection limit");
        Ensure(exhausted == 1, "server should reject the overflow call as ResourceExhausted");
        Ensure(await svc.AddAsync(20, 22) == 42, "connection should recover after call capacity is released");
    }

    [Test]
    [NotInParallel]
    public async Task AdmissionQueueShouldRemainBoundedAndRecoverOnSameConnection()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();

        var active = service.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(75);
        var queued = service.SlowAddWithoutTimeoutAsync(20, 2).AsTask();
        await Task.Delay(75);
        await EnsureThrowsSharpLinkFast(
            service.AddAsync(20, 3).AsTask(),
            "admission queue count",
            SharpLinkErrorCode.ResourceExhausted);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 21, "active admitted call");
        Ensure(await queued.WaitAsync(TimeSpan.FromSeconds(2)) == 22, "queued admitted call");
        Ensure(await service.AddAsync(20, 4) == 24, "connection recovers after overload");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamShouldSpoolUntilAdmissionAndPreserveOrder()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();

        var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var queuedStream = service.UploadAsync(
            ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2, "active call before stream");
        Ensure(await queuedStream.WaitAsync(TimeSpan.FromSeconds(2)) == 10,
            "pre-admission stream spool order");
        Ensure(TestService.ActiveUploads == 0, "queued stream permit and dispatcher released");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedDuplexStreamShouldSpoolAndHoldPermitForBothDirections()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x11, 256).ToArray(),
            Enumerable.Repeat((byte)0x22, 512).ToArray()
        };
        var duplex = CollectAsync(
            harness.Client.Get<ICompressionService>().DuplexBytesAsync(
                ToAsyncEnumerable(payloads, CancellationToken.None)),
            CancellationToken.None);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2,
            "active call before queued duplex");
        var received = await duplex.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(received.Count == 2 && received[0].SequenceEqual(payloads[0]) &&
            received[1].SequenceEqual(payloads[1]), "queued duplex preserves both directions");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "duplex releases admission permit");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedCompressedRequestAndClientStreamShouldDecodeAfterAdmission()
    {
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));

        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(75);
        var item = Enumerable.Repeat((byte)0x2a, 8192).ToArray();
        var upload = harness.Client.Get<ICompressionService>().UploadBytesAsync(
            ToAsyncEnumerable([item, item, item], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
            "active call before compressed stream");
        Ensure(await upload.WaitAsync(TimeSpan.FromSeconds(2)) == item.Length * 3,
            "queued compressed request and stream items");
        Ensure((await harness.Client.Get<ICompressionService>().EchoBytesAsync([1, 2, 3]))
            .SequenceEqual(new byte[] { 1, 2, 3 }), "combined overload connection recovery");
    }

    [Test]
    public async Task MethodRateLimitShouldNotThrottleOtherMethodsAndShouldRecover()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddMethod<ITestService>(nameof(ITestService.AddAsync), rule =>
                    rule.UseFixedWindow(rate =>
                    {
                        rate.PermitLimit = 1;
                        rate.Window = TimeSpan.FromMilliseconds(100);
                    }))));
        var service = harness.Client.Get<ITestService>();

        Ensure(await service.AddAsync(1, 1) == 2, "first method-rate permit");
        await EnsureThrowsSharpLinkFast(
            service.AddAsync(2, 2).AsTask(),
            "method rate rejection",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure((await service.EchoAsync(new Person { Name = "other", Age = 1 })).Age == 2,
            "unlimited method remains healthy");
        await Task.Delay(150);
        Ensure(await service.AddAsync(3, 4) == 7, "method rate replenishment");
    }

    [Test]
    public async Task ContractLimitShouldNotThrottleAnotherContract()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddContract<ITestService>(rule => rule.UseConcurrency(1))));
        var testService = harness.Client.Get<ITestService>();
        var active = testService.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            testService.AddAsync(1, 1).AsTask(),
            "contract concurrency rejection",
            SharpLinkErrorCode.ResourceExhausted);
        var other = await harness.Client.Get<ICompressionService>().EchoBytesAsync([7, 8, 9]);
        Ensure(other.SequenceEqual(new byte[] { 7, 8, 9 }), "other contract remains admitted");
        Ensure(await active == 21, "contract permit owner completes");
    }

    [Test]
    public async Task PartitionSelectorShouldIsolateMetadataKeys()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.UsePartition(
                context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
                partition =>
                {
                    partition.MaxPartitions = 8;
                    partition.UseConcurrency(1);
                })));
        var service = harness.Client.Get<ITestService>();
        using var cancellation = new CancellationTokenSource();
        var tenantA = new SharpLinkCallOptions
        {
            Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "a"))
        };
        var tenantB = new SharpLinkCallOptions
        {
            Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "b"))
        };
        var active = service.SlowAddWithOptionsAsync(1, 2, tenantA, cancellation.Token).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            service.DescribeCallAsync(1, tenantA, CancellationToken.None).AsTask(),
            "same partition concurrency",
            SharpLinkErrorCode.ResourceExhausted);
        var other = await service.DescribeCallAsync(2, tenantB, CancellationToken.None);
        Ensure(other.StartsWith("2:b:", StringComparison.Ordinal), "independent partition permit");
        cancellation.Cancel();
        await EnsureThrows<OperationCanceledException>(active, "partition active cancellation");
    }

    [Test]
    public async Task AdmissionQueueByteLimitShouldRejectBeforeServiceCreation()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 2;
                options.MaxQueuedBytes = 64;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            service.EchoAsync(new Person
            {
                Name = new string('x', 2048),
                Age = 1,
                Tags = ["queue-bytes"]
            }).AsTask(),
            "admission queue bytes",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await active == 11, "queue-byte permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "queue-byte rejection connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PreAdmissionStreamSpoolShouldRejectWhenRetainedBytesOverflow()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 128;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);
        var oversized = service.UploadAsync(ToAsyncEnumerable(
            Enumerable.Range(1, 100), CancellationToken.None)).AsTask();

        // The initial request fits, then the pre-admission stream frames consume the
        // remaining retained-byte budget and terminate the call without service execution.
        await EnsureThrowsSharpLinkFast(
            oversized,
            "pre-admission stream retained bytes",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(TestService.ActiveUploads == 0, "overflowed stream service did not execute");
        Ensure(await active == 11, "spool overflow permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "spool overflow connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedCancellationAndDeadlineShouldNotLeakPermits()
    {
        await using (var cancellationHarness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            })))
        {
            var service = cancellationHarness.Client.Get<ITestService>();
            var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
            await Task.Delay(75);
            using var cancellation = new CancellationTokenSource();
            var queued = service.SlowAddAsync(2, 2, cancellation.Token).AsTask();
            cancellation.CancelAfter(50);
            await EnsureThrows<OperationCanceledException>(queued, "queued cancellation");
            Ensure(await active == 2, "active call after queued cancellation");
            Ensure(await service.AddAsync(3, 4) == 7, "permit after queued cancellation");
        }

        TestService.ResetNonCancellableCompletion();
        await using var deadlineHarness = await TestHarness.CreateAsync(
            requestTimeout: TimeSpan.FromMilliseconds(100),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var deadlineService = deadlineHarness.Client.Get<ITestService>();
        var deadlineActive = deadlineService.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(25);
        await EnsureThrowsSharpLinkFast(
            deadlineService.AddAsync(2, 2).AsTask(),
            "queued deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await EnsureThrowsSharpLinkFast(
            deadlineActive,
            "active default deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task RejectedAndQueuedOneWayCallsShouldFollowConfiguredPolicy()
    {
        TestService.ResetNotify();
        await using (var rejectHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1))))
        {
            var service = rejectHarness.Client.Get<ITestService>();
            var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
            await Task.Delay(75);
            await service.NotifyAsync("drop");
            await Task.Delay(75);
            Ensure(TestService.NotifyCount == 0, "rejected OneWay service must not execute");
            Ensure(await active == 2, "OneWay rejection permit owner");
        }

        TestService.ResetNotify();
        await using var queueHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var queuedService = queueHarness.Client.Get<ITestService>();
        var queuedActive = queuedService.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
        await Task.Delay(75);
        await queuedService.NotifyAsync("queue");
        Ensure(await queuedActive == 4, "queued OneWay permit owner");
        await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(TestService.NotifyCount == 1, "explicitly queued OneWay executes once");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayClientStreamShouldSpoolUntilAdmission()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(4, 5).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x31, 128).ToArray(),
            Enumerable.Repeat((byte)0x32, 256).ToArray()
        };

        await harness.Client.Get<ICompressionService>().NotifyStreamBytesAsync(
            ToAsyncEnumerable(payloads, CancellationToken.None));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 9,
            "queued OneWay client-stream permit owner");
        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == 384,
            "queued OneWay client-stream items preserved");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "queued OneWay client-stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedOneWayClientStreamShouldDrainWithoutServiceExecution()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder => builder.UseAdmissionControl(options =>
                options.Global.UseConcurrency(1)));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(6, 7).AsTask();
        await Task.Delay(75);
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamBytesAsync(ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 13,
            "rejected OneWay stream permit owner");
        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "rejected OneWay stream service must not execute");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "rejected OneWay stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PostAdmissionArgumentDecodeFailureShouldReleaseReservedStreams()
    {
        TestService.ResetMalformedUploadInvocations();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
        {
            builder.UseCodec(new ThrowingPersonCodec());
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            });
        });
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(8, 9).AsTask();
        await Task.Delay(75);
        var failed = service.UploadWithHeaderAsync(
            new Person { Name = "malformed", Age = 1 },
            ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();

        await EnsureThrowsSharpLinkFast(
            failed,
            "post-admission argument decode failure",
            SharpLinkErrorCode.Internal);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 17,
            "post-admission decode permit owner");
        Ensure(TestService.MalformedUploadInvocations == 0,
            "malformed request service must not execute");
        Ensure(await service.AddAsync(20, 22) == 42,
            "post-admission decode failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayRequestDecompressionFailureShouldDrainReservedStreams()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var permitOwner = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(9, 10).AsTask();
        await Task.Delay(75);
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());
        var failedOneWay = harness.Client.Get<ICompressionService>()
            .NotifyStreamWithHeaderAsync(
                Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask();

        Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 19,
            "queued compressed OneWay permit owner");
        await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "failed compressed OneWay request must not execute the service");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "compressed OneWay decode failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task OneWayRequestDecompressionFailureWithoutAdmissionShouldDrainClientStreams()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamWithHeaderAsync(
                Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "failed compressed OneWay request must not execute without admission");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "non-admission compressed OneWay failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayStubFailureShouldDrainReservedStreams()
    {
        TestService.ResetMalformedOneWayInvocations();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder =>
            {
                builder.UseCodec(new ThrowingPersonCodec());
                builder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 1;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                    options.QueueOneWayCalls = true;
                });
            });
        var service = harness.Client.Get<ITestService>();
        var permitOwner = service.SlowAddWithoutTimeoutAsync(10, 11).AsTask();
        await Task.Delay(75);
        var failedOneWay = service.NotifyUploadWithHeaderAsync(
            new Person { Name = "malformed-oneway", Age = 1 },
            ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();

        Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 21,
            "queued malformed OneWay permit owner");
        await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Ensure(TestService.MalformedOneWayInvocations == 0,
            "malformed OneWay request must not execute the service");
        Ensure(await service.AddAsync(20, 22) == 42,
            "malformed OneWay stub failure connection recovery");
    }

    [Test]
    public async Task ServerStreamEarlyBreakShouldReleaseAdmissionPermit()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        var service = harness.Client.Get<ITestService>();
        await using (var enumerator = service.SlowDownloadAsync(
            20, 50, CancellationToken.None).GetAsyncEnumerator())
        {
            Ensure(await enumerator.MoveNextAsync(), "admitted server stream first item");
            await EnsureThrowsSharpLinkFast(
                service.AddAsync(1, 1).AsTask(),
                "permit held for server stream",
                SharpLinkErrorCode.ResourceExhausted);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Ensure(await service.AddAsync(20, 22) == 42, "permit after stream early break");
                return;
            }
            catch (SharpLinkException exception) when (
                exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                await Task.Delay(10);
            }
        }
        throw new Exception("assert failed: stream early-break permit was not released");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldCancelAdmissionWaitersWithoutUnboundedDelay()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
        await Task.Delay(50);

        var started = Stopwatch.GetTimestamp();
        await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2), "bounded stop with waiter");
        await EnsureThrows<SharpLinkException>(queued, "queued call stopped before execution");
        await EnsureThrows<SharpLinkException>(active, "active call disconnected by forced stop");
    }

    [Test]
    [NotInParallel]
    public async Task ClientDisconnectShouldCancelAdmissionWaiterAndAllowBoundedServerStop()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
        await Task.Delay(50);

        await harness.DisposeClientOnlyAsync();
        await EnsureThrows<SharpLinkException>(queued, "disconnected admission waiter");
        await EnsureThrows<SharpLinkException>(active, "disconnected active call");
        await harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(1))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task EnsureClientStreamProducerFailure(Task task, string name)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should fail");
        }
        catch (InvalidOperationException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Internal)
        {
        }
    }

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name, SharpLinkErrorCode errorCode)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> values, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> YieldOneThenWaitAsync(
        int value,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return value;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<int> YieldThenFailAsync(int value)
    {
        yield return value;
        await Task.Yield();
        throw new InvalidOperationException("injected client stream producer failure");
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }

        private TestHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static async Task<TestHarness> CreateAsync(
            TimeSpan? requestTimeout = null,
            Func<Type, IRpcCodec?>? codecResolver = null,
            Action<SharpLinkRuntimeOptions>? runtimeConfigure = null,
            Action<SharpLinkConnectionPoolOptions>? poolConfigure = null,
            bool disableRequestTimeout = false,
            bool useSharedMemory = false,
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null)
        {
            codecResolver ??= MemoryPackCodec.Resolver;
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseSerializer(codecResolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (runtimeConfigure is not null)
                serverBuilder.UseRuntime(runtimeConfigure);
            if (serverRuntimeConfigure is not null)
                serverBuilder.UseRuntime(serverRuntimeConfigure);
            serverConfigure?.Invoke(serverBuilder);

            var sharedMemoryName = $"sharplink-behavior-{Guid.NewGuid():N}";
            var port = 0;
            if (useSharedMemory)
                serverBuilder.UseSharedMemory(sharedMemoryName);
            else
            {
                serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
                port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            }
            var server = serverBuilder.Build();

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
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
                .UseSerializer(codecResolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (useSharedMemory)
                clientBuilder.UseSharedMemory(sharedMemoryName);
            else
                clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
            if (runtimeConfigure is not null)
                clientBuilder.UseRuntime(runtimeConfigure);
            if (clientRuntimeConfigure is not null)
                clientBuilder.UseRuntime(clientRuntimeConfigure);
            if (poolConfigure is not null)
                clientBuilder.UseConnectionPool(poolConfigure);

            if (disableRequestTimeout)
                clientBuilder.DisableRequestTimeout();
            else if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);

            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new TestHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeServerOnlyAsync(TimeSpan? gracefulTimeout = null)
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            await _server.StopAsync(gracefulTimeout ?? TimeSpan.Zero);
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            await Client.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            await _serverCts.CancelAsync();
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }

    private sealed class CountingCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _compressCount;
        private int _decompressCount;
        public string Algorithm => inner.Algorithm;
        public int CompressCount => Volatile.Read(ref _compressCount);
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public async ValueTask<SharpLinkCompressionResult> CompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            return await inner.CompressAsync(input, output, maxOutputBytes, cancellationToken);
        }

        public async ValueTask<SharpLinkCompressionResult> DecompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            return await inner.DecompressAsync(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class NoBenefitCompressionProvider : ISharpLinkCompressionProvider
    {
        public string Algorithm => "test.identity/v1";

        public ValueTask<SharpLinkCompressionResult> CompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            foreach (var segment in input)
                output.Write(segment.Span);
            return ValueTask.FromResult(new SharpLinkCompressionResult(
                checked((int)input.Length), checked((int)input.Length)));
        }

        public ValueTask<SharpLinkCompressionResult> DecompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Identity candidates must never be selected.");
    }

    private sealed class ThrowingCompressionProvider(
        ISharpLinkCompressionProvider inner,
        bool throwOnCompress,
        bool throwOnDecompress) : ISharpLinkCompressionProvider
    {
        public string Algorithm => inner.Algorithm;

        public ValueTask<SharpLinkCompressionResult> CompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throwOnCompress
                ? throw new InvalidOperationException("Injected compression failure.")
                : inner.CompressAsync(input, output, maxOutputBytes, cancellationToken);

        public ValueTask<SharpLinkCompressionResult> DecompressAsync(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throwOnDecompress
                ? throw new InvalidOperationException("Injected decompression failure.")
                : inner.DecompressAsync(input, output, maxOutputBytes, cancellationToken);
    }

    private sealed class ThrowingPersonCodec : IRpcCodec<Person>
    {
        public void Serialize(in Person value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException("The server never serializes this request argument.");

        public Person? Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new InvalidDataException("Injected request argument decode failure.");
    }

    private sealed class MarkerPersonCodec(byte marker) : IRpcCodec<Person>
    {
        private readonly byte _marker = marker;
        public int SerializeCount;
        public int DeserializeCount;

        public void Serialize(in Person value, IBufferWriter<byte> buffer)
        {
            var markerSpan = buffer.GetSpan(1);
            markerSpan[0] = _marker;
            buffer.Advance(1);
            MemoryPackCodec<Person>.Instance.Serialize(value, buffer);
            Interlocked.Increment(ref SerializeCount);
        }

        public Person? Deserialize(in ReadOnlySequence<byte> buffer)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryRead(out var actualMarker) || actualMarker != _marker)
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DTO codec marker mismatch.");
            Interlocked.Increment(ref DeserializeCount);
            var payload = buffer.Slice(reader.Position);
            return MemoryPackCodec<Person>.Instance.Deserialize(payload);
        }
    }
}

[RpcContract]
public interface ITestService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [Sdk.Timeout]
    ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> SlowThrowWithoutTimeoutAsync();
    [NonCancellable]
    ValueTask ThrowCancellationAsync();
    ValueTask<int> SlowAddWithOptionsAsync(
        int left,
        int right,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken);
    ValueTask<string> DescribeCallAsync(
        int value,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<Person> EchoAsync(Person person);
    [NonCancellable]
    ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value);
    [NonCancellable]
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    ValueTask<int> UploadWithHeaderAsync(Person header, IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<string> DownloadAsync(int count);
    IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, CancellationToken cancellationToken);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(string message);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyUploadWithHeaderAsync(Person header, IAsyncEnumerable<int> values);
}

[RpcService]
public class TestService : ITestService
{
    private static TaskCompletionSource s_nonCancellableCompletion = CreateCompletionSource();
    private static TaskCompletionSource s_nonCancellableFailure = CreateCompletionSource();
    private static TaskCompletionSource s_downloadDisposed = CreateCompletionSource();
    private static int s_activeUploads;
    private static int s_malformedUploadInvocations;
    private static int s_malformedOneWayInvocations;
    private static int s_notifyCount;
    private static TaskCompletionSource s_notify = CreateCompletionSource();

    internal static int ActiveUploads => Volatile.Read(ref s_activeUploads);
    internal static int MalformedUploadInvocations => Volatile.Read(ref s_malformedUploadInvocations);
    internal static int MalformedOneWayInvocations => Volatile.Read(ref s_malformedOneWayInvocations);
    internal static int NotifyCount => Volatile.Read(ref s_notifyCount);

    internal static void ResetNotify()
    {
        Volatile.Write(ref s_notifyCount, 0);
        Interlocked.Exchange(ref s_notify, CreateCompletionSource());
    }

    internal static void ResetMalformedOneWayInvocations()
        => Volatile.Write(ref s_malformedOneWayInvocations, 0);

    internal static Task WaitForNotifyAsync() => Volatile.Read(ref s_notify).Task;

    internal static void ResetActiveUploads() => Volatile.Write(ref s_activeUploads, 0);
    internal static void ResetMalformedUploadInvocations()
        => Volatile.Write(ref s_malformedUploadInvocations, 0);

    internal static void ResetNonCancellableCompletion()
        => Interlocked.Exchange(ref s_nonCancellableCompletion, CreateCompletionSource());

    internal static Task WaitForNonCancellableCompletionAsync()
        => Volatile.Read(ref s_nonCancellableCompletion).Task;

    internal static void ResetNonCancellableFailure()
        => Interlocked.Exchange(ref s_nonCancellableFailure, CreateCompletionSource());

    internal static Task WaitForNonCancellableFailureAsync()
        => Volatile.Read(ref s_nonCancellableFailure).Task;

    internal static void ResetDownloadDisposed()
        => Interlocked.Exchange(ref s_downloadDisposed, CreateCompletionSource());

    internal static Task WaitForDownloadDisposedAsync()
        => Volatile.Read(ref s_downloadDisposed).Task;

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableCompletion).TrySetResult();
        return left + right;
    }

    public async ValueTask<int> SlowThrowWithoutTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableFailure).TrySetResult();
        throw new InvalidOperationException("late non-cancellable failure");
    }

    public ValueTask ThrowCancellationAsync()
        => ValueTask.FromException(new OperationCanceledException("service-specific cancellation"));

    public async ValueTask<int> SlowAddWithOptionsAsync(
        int left,
        int right,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public ValueTask<string> DescribeCallAsync(
        int value,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var context = SharpLinkCallContext.Current;
        var tenant = context?.Metadata is { Count: > 0 } metadata
            ? metadata[0].Value
            : "missing";
        var deadline = context?.Deadline is null ? "no-deadline" : "deadline";
        return ValueTask.FromResult($"{value}:{tenant}:{deadline}");
    }

    public ValueTask<Person> EchoAsync(Person person)
    {
        person.Name += "-r";
        person.Age += 1;
        return ValueTask.FromResult(person);
    }

    public ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value)
        => ValueTask.FromResult(value with { Name = value.Name + "-r", Age = value.Age + 1 });

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        Interlocked.Increment(ref s_activeUploads);
        try
        {
            var sum = 0;
            await foreach (var i in values) sum += i;
            return sum;
        }
        finally
        {
            Interlocked.Decrement(ref s_activeUploads);
        }
    }

    public async ValueTask<int> UploadWithHeaderAsync(
        Person header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedUploadInvocations);
        var sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async ValueTask NotifyUploadWithHeaderAsync(
        Person header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedOneWayInvocations);
        await foreach (var value in values)
            _ = value;
    }

    public async IAsyncEnumerable<string> DownloadAsync(int count)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                yield return $"v-{i}";
                await Task.Yield();
            }
        }
        finally
        {
            Volatile.Read(ref s_downloadDisposed).TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    public ValueTask NotifyAsync(string message)
    {
        _ = message;
        Interlocked.Increment(ref s_notifyCount);
        Volatile.Read(ref s_notify).TrySetResult();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[RpcContract]
public interface ICompressionService : IService
{
    [NonCancellable]
    ValueTask<byte[]> EchoBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamWithHeaderAsync(byte[] header, IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values);
}

[RpcService]
public sealed class CompressionService : ICompressionService
{
    private static TaskCompletionSource<int> s_oneWay = CreateOneWayCompletion();

    internal static void ResetOneWay()
        => Interlocked.Exchange(ref s_oneWay, CreateOneWayCompletion());

    internal static Task<int> WaitForOneWayAsync() => Volatile.Read(ref s_oneWay).Task;

    public ValueTask<byte[]> EchoBytesAsync(byte[] value) => ValueTask.FromResult(value);

    public ValueTask NotifyBytesAsync(byte[] value)
    {
        Volatile.Read(ref s_oneWay).TrySetResult(value.Length);
        return ValueTask.CompletedTask;
    }

    public async ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask NotifyStreamWithHeaderAsync(
        byte[] header,
        IAsyncEnumerable<byte[]> values)
    {
        var total = header.Length;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        return total;
    }

    public async IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size)
    {
        var payload = Enumerable.Repeat((byte)0x2a, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            yield return payload;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        await foreach (var value in values)
            yield return value;
    }

    private static TaskCompletionSource<int> CreateOneWayCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[MemoryPackable]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed record GeneratedAddress(
    [property: RpcMember(1)] string City);

public sealed record GeneratedEnvelope(
    [property: RpcRequired] string Name,
    int Age,
    GeneratedAddress Address,
    List<string> Tags);
