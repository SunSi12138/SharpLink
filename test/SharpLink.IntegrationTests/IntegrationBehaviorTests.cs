namespace SharpLink.IntegrationTests;

public class IntegrationBehaviorTests
{
    [Test]
    public async Task BasicRpcAndStreamingShouldWork()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var add = await svc.AddAsync(10, 20);
        Ensure(add == 30, "AddAsync");

        var echo = await svc.EchoAsync(new Person { Name = "s", Age = 1, Tags = ["x"] });
        Ensure(echo is { Name: "s-r", Age: 2 }, "EchoAsync");

        var sum = await svc.UploadAsync(ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None));
        Ensure(sum == 10, "UploadAsync");

        var values = await CollectAsync(svc.DownloadAsync(3), CancellationToken.None);
        Ensure(values.SequenceEqual(["v-0", "v-1", "v-2"]), "DownloadAsync");

        await svc.NotifyAsync("ok");
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
    public async Task UnaryWithoutTimeoutAttributeShouldUseClientDefaultTimeout()
    {
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithoutTimeoutAsync(1, 2).AsTask(),
            "SlowAddWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);
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
            SharpLinkErrorCode.ConnectionClosed);
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
            Func<Type, IRpcCodec?>? codecResolver = null)
        {
            codecResolver ??= MemoryPackCodec.Resolver;
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .AddService<ITestService, TestService>()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(codecResolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
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
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseSerializer(codecResolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

            if (requestTimeout is { } timeout)
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
    ValueTask<int> AddAsync(int left, int right);
    [Sdk.Timeout]
    ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken);
    ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right);
    ValueTask<int> SlowAddWithOptionsAsync(
        int left,
        int right,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken);
    ValueTask<string> DescribeCallAsync(
        int value,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken);
    ValueTask<Person> EchoAsync(Person person);
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);
    IAsyncEnumerable<string> DownloadAsync(int count);
    IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, CancellationToken cancellationToken);
    [Oneway]
    ValueTask NotifyAsync(string message);
}

[RpcService]
public class TestService : ITestService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        return left + right;
    }

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

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var i in values) sum += i;
        return sum;
    }

    public async IAsyncEnumerable<string> DownloadAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"v-{i}";
            await Task.Yield();
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
}

[MemoryPackable]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}
