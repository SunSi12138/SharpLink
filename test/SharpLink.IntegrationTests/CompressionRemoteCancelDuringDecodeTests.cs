using System.Reflection;

namespace SharpLink.IntegrationTests;

public class CompressionRemoteCancelDuringDecodeTests
{
    [Test]
    [NotInParallel]
    public async Task RemoteCancelShouldReachPostCapacityCompressedDecode()
    {
        CompressionMergeGateProbeService.Reset();
        var serverProvider = new WaitForRemoteCancellationProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await RemoteCancelHarness.CreateAsync(serverProvider);
        var payload = Enumerable.Repeat((byte)0x61, 32 * 1024).ToArray();
        using var callCts = new CancellationTokenSource();
        var call = harness.Client.Get<ICompressionMergeGateProbeService>()
            .CancellableEchoAsync(payload, callCts.Token)
            .AsTask();

        await serverProvider.WaitForDecompressionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => harness.ActiveCalls == 1,
            "compressed cancellable request should own call capacity while decode is running");

        callCts.Cancel();

        await serverProvider.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await ObserveTerminalCallAsync(call);
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "remote-cancelled compressed decode should release call capacity");
        Ensure(CompressionMergeGateProbeService.CancellableEchoInvocations == 0,
            "remote-cancelled compressed decode must not invoke the service");

        var response = await harness.Client.Get<ICompressionMergeGateProbeService>()
            .EchoAsync(payload)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(response.SequenceEqual(payload),
            "connection should remain reusable after remote cancellation during compressed decode");
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0,
            "reusable control call should release call capacity");
    }

    private static async Task ObserveTerminalCallAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.Cancelled or SharpLinkErrorCode.ConnectionClosed)
        {
        }
        catch (TimeoutException)
        {
            throw new Exception("assert failed: remote-cancelled call did not terminate");
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
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class WaitForRemoteCancellationProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly TaskCompletionSource _decompressionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempt;

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
            if (Interlocked.Increment(ref _attempt) != 1)
                return inner.Decompress(input, output, maxOutputBytes, cancellationToken);

            _decompressionStarted.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException(
                    "The server-side compressed decode token was not cancelled by the remote caller.");
            }

            _cancellationObserved.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class RemoteCancelHarness : IAsyncDisposable
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        public ISharpLinkClient Client { get; }
        public int ActiveCalls
            => (int)(GetRequiredField(_server, "_globalActiveCalls").GetValue(_server)
                ?? throw new Exception("server active call count is null"));

        private RemoteCancelHarness(
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

        public static async Task<RemoteCancelHarness> CreateAsync(
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
                .UseConnectionPool(options =>
                {
                    options.MinConnections = 1;
                    options.MaxConnections = 1;
                })
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()))
                .Build();
            await client.ConnectAsync();

            return new RemoteCancelHarness(serverCts, serverTask, server, client);
        }

        private static FieldInfo GetRequiredField(object instance, string name)
            => instance.GetType().GetField(name, InstanceFlags)
               ?? throw new Exception($"cannot find field {name}");

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
