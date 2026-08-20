namespace SharpLink.IntegrationTests;

public class CompressionCallCapacityAdmissionTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompressedUnaryShouldDecompressOnlyAfterCallCapacityAdmission(
        bool useAdvancedAdmission)
    {
        TestService.ResetBlockingAdd();
        var serverProvider = new CountingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await CapacityHarness.CreateAsync(
            serverProvider,
            useAdvancedAdmission);
        var blocker = harness.Client.Get<ITestService>()
            .BlockingAddAsync(1, 2, CancellationToken.None)
            .AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var payload = Enumerable.Repeat((byte)0x41, 32 * 1024).ToArray();

            await EnsureResourceExhaustedAsync(
                harness.Client.Get<ICompressionService>().EchoBytesAsync(payload).AsTask(),
                "compressed unary capacity rejection");

            Ensure(serverProvider.DecompressCount == 0,
                "capacity-rejected compressed unary request must not be decompressed");

            TestService.ReleaseBlockingAdd();
            Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(2)) == 3,
                "capacity owner should complete after release");

            var response = await harness.Client.Get<ICompressionService>()
                .EchoBytesAsync(payload)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(response.SequenceEqual(payload), "accepted compressed unary response");
            Ensure(serverProvider.DecompressCount == 1,
                "accepted compressed unary request must be decompressed exactly once");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompressedOneWayShouldDecompressOnlyAfterCallCapacityAdmission(
        bool useAdvancedAdmission)
    {
        TestService.ResetBlockingAdd();
        CompressionService.ResetOneWay();
        var serverProvider = new CountingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await CapacityHarness.CreateAsync(
            serverProvider,
            useAdvancedAdmission);
        var blocker = harness.Client.Get<ITestService>()
            .BlockingAddAsync(3, 4, CancellationToken.None)
            .AsTask();

        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var payload = Enumerable.Repeat((byte)0x42, 32 * 1024).ToArray();

            await harness.Client.Get<ICompressionService>()
                .NotifyBytesAsync(payload)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => harness.RejectedOneWayCalls == 1,
                "compressed one-way capacity rejection");

            Ensure(serverProvider.DecompressCount == 0,
                "capacity-rejected compressed one-way request must not be decompressed");
            Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
                "capacity-rejected compressed one-way request must not execute the service");

            TestService.ReleaseBlockingAdd();
            Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(2)) == 7,
                "capacity owner should complete after release");

            CompressionService.ResetOneWay();
            await harness.Client.Get<ICompressionService>()
                .NotifyBytesAsync(payload)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) ==
                   payload.Length,
                "accepted compressed one-way request should execute");
            Ensure(serverProvider.DecompressCount == 1,
                "accepted compressed one-way request must be decompressed exactly once");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task CompressedUnaryShouldRejectIfDeadlineExpiresDuringDecompression()
    {
        DeadlineCompressionProbeService.Reset();
        var serverProvider = new BlockingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        var requestTimeout = TimeSpan.FromMilliseconds(100);
        await using var harness = await CapacityHarness.CreateAsync(
            serverProvider,
            useAdvancedAdmission: false,
            requestTimeout);
        var payload = Enumerable.Repeat((byte)0x43, 32 * 1024).ToArray();
        var call = harness.Client.Get<IDeadlineCompressionProbeService>()
            .EchoAsync(payload)
            .AsTask();

        try
        {
            await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await EnsureDeadlineExceededAsync(call, "compressed unary post-decode deadline");
            serverProvider.ReleaseDecompression();
            await WaitUntilAsync(() => harness.ActiveCalls == 0, "expired unary call release");

            Ensure(DeadlineCompressionProbeService.UnaryInvocations == 0,
                "expired compressed unary request must not execute the service");
        }
        finally
        {
            serverProvider.ReleaseDecompression();
        }
    }

    [Test]
    [NotInParallel]
    public async Task CompressedOneWayShouldDropIfDeadlineExpiresDuringDecompression()
    {
        DeadlineCompressionProbeService.Reset();
        var serverProvider = new BlockingDecompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        var requestTimeout = TimeSpan.FromMilliseconds(100);
        await using var harness = await CapacityHarness.CreateAsync(
            serverProvider,
            useAdvancedAdmission: false,
            requestTimeout);
        var payload = Enumerable.Repeat((byte)0x44, 32 * 1024).ToArray();

        try
        {
            await harness.Client.Get<IDeadlineCompressionProbeService>()
                .NotifyAsync(payload)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(requestTimeout + TimeSpan.FromMilliseconds(150));
            serverProvider.ReleaseDecompression();
            await WaitUntilAsync(() => harness.ActiveCalls == 0, "expired one-way call release");

            Ensure(DeadlineCompressionProbeService.OneWayInvocations == 0,
                "expired compressed one-way request must not execute the service");
        }
        finally
        {
            serverProvider.ReleaseDecompression();
        }
    }

    private static async Task EnsureResourceExhaustedAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted,
                $"{scenario} should return ResourceExhausted, actual {exception.Code}");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not fail fast");
        }
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

    private sealed class CountingCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _decompressCount;

        public string WireProfile => inner.WireProfile;
        public int DecompressCount => Volatile.Read(ref _decompressCount);

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
            Interlocked.Increment(ref _decompressCount);
            return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class BlockingDecompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public string WireProfile => inner.WireProfile;

        public Task WaitForDecompressionAsync() => _decompressionStarted.Task;

        public void ReleaseDecompression() => _release.Set();

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
            _release.Wait();
            return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class CapacityHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        public ISharpLinkClient Client { get; }
        public long RejectedOneWayCalls
        {
            get
            {
                var reflectionField = _server.GetType().GetField(
                    "_rejectedOneWayCalls",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("cannot find rejected one-way call counter");
                return (long)reflectionField.GetValue(_server)!;
            }
        }
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

        private CapacityHarness(
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

        public static async Task<CapacityHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            bool useAdvancedAdmission,
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

            var clientBuilder = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()));
            if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);
            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new CapacityHarness(serverCts, serverTask, server, client);
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

[RpcContract]
public interface IDeadlineCompressionProbeService : IService
{
    [NonCancellable]
    ValueTask<byte[]> EchoAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(byte[] value);
}

[RpcService]
public sealed class DeadlineCompressionProbeService : IDeadlineCompressionProbeService
{
    private static int s_unaryInvocations;
    private static int s_oneWayInvocations;

    internal static int UnaryInvocations => Volatile.Read(ref s_unaryInvocations);
    internal static int OneWayInvocations => Volatile.Read(ref s_oneWayInvocations);

    internal static void Reset()
    {
        Volatile.Write(ref s_unaryInvocations, 0);
        Volatile.Write(ref s_oneWayInvocations, 0);
    }

    public ValueTask<byte[]> EchoAsync(byte[] value)
    {
        Interlocked.Increment(ref s_unaryInvocations);
        return ValueTask.FromResult(value);
    }

    public ValueTask NotifyAsync(byte[] value)
    {
        _ = value;
        Interlocked.Increment(ref s_oneWayInvocations);
        return ValueTask.CompletedTask;
    }
}
