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
            bool useSharedMemory = false)
        {
            codecResolver ??= MemoryPackCodec.Resolver;
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseSerializer(codecResolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (runtimeConfigure is not null)
                serverBuilder.UseRuntime(runtimeConfigure);

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
    IAsyncEnumerable<string> DownloadAsync(int count);
    IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, CancellationToken cancellationToken);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(string message);
}

[RpcService]
public class TestService : ITestService
{
    private static TaskCompletionSource s_nonCancellableCompletion = CreateCompletionSource();
    private static TaskCompletionSource s_nonCancellableFailure = CreateCompletionSource();
    private static TaskCompletionSource s_downloadDisposed = CreateCompletionSource();
    private static int s_activeUploads;

    internal static int ActiveUploads => Volatile.Read(ref s_activeUploads);

    internal static void ResetActiveUploads() => Volatile.Write(ref s_activeUploads, 0);

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
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CreateCompletionSource()
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
