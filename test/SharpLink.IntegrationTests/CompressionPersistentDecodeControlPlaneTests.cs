namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeControlPlaneTests
{
    private const int SmallPayloadBytes = 64 * 1024;
    private const int LargePayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task CurrentCutoverShouldKeep64KiBInlineAndRoute2MiBThroughPersistentExecutor()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), initiallyReleased: true);
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        var service = harness.Client.Get<IPersistentDecodeControlPlaneService>();

        using var smallCancellation = new CancellationTokenSource();
        var small = Enumerable.Repeat((byte)0x31, SmallPayloadBytes).ToArray();
        Ensure(await service.MeasureAsync(small, smallCancellation.Token) == small.Length,
            "64KiB compressed request result");
        Ensure(harness.DecodeStartedWorkCount == 0,
            "64KiB request must remain on inline B at the current conservative cutover");

        using var largeCancellation = new CancellationTokenSource();
        var large = Enumerable.Repeat((byte)0x32, LargePayloadBytes).ToArray();
        Ensure(await service.MeasureAsync(large, largeCancellation.Token) == large.Length,
            "2MiB compressed request result");
        Ensure(harness.DecodeStartedWorkCount == 1,
            "2MiB request must execute through persistent D at the current conservative cutover");
        Ensure(PersistentDecodeControlPlaneService.Invocations == 2,
            "both routing paths must invoke the service exactly once");
        await AssertResourcesReleasedAsync(harness, "cutover routing");
    }

    [Test]
    [NotInParallel]
    public async Task RunningPersistentDecodeShouldObserveRemoteCancelFromRequestLoop()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        using var cancellation = new CancellationTokenSource();
        var call = harness.Client.Get<IPersistentDecodeControlPlaneService>()
            .MeasureAsync(CreateLargePayload(0x41), cancellation.Token)
            .AsTask();

        try
        {
            await serverProvider.WaitForStartedCountAsync(1);
            await cancellation.CancelAsync();
            await serverProvider.WaitForCancellationCountAsync(1);
            await EnsureRemoteCancelledAsync(call, "running persistent decode remote cancel");
            await AssertResourcesReleasedAsync(harness, "running remote cancel");
            Ensure(PersistentDecodeControlPlaneService.Invocations == 0,
                "remote-cancelled decode must not invoke the service");
        }
        finally
        {
            serverProvider.ReleaseAll();
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedPersistentDecodeShouldCancelBeforeProviderStart()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        var workerCount = harness.DecodeWorkerCount;
        var service = harness.Client.Get<IPersistentDecodeControlPlaneService>();
        var blockerCancellations = Enumerable.Range(0, workerCount)
            .Select(static _ => new CancellationTokenSource())
            .ToArray();
        var blockers = blockerCancellations
            .Select((cancellation, index) => service.MeasureAsync(
                    CreateLargePayload((byte)(0x50 + index)), cancellation.Token)
                .AsTask())
            .ToArray();
        using var queuedCancellation = new CancellationTokenSource();

        try
        {
            await serverProvider.WaitForStartedCountAsync(workerCount);
            Ensure(harness.DecodeStartedWorkCount == workerCount,
                "all persistent workers must be occupied before queueing the cancellation probe");

            var queued = service.MeasureAsync(CreateLargePayload(0x60), queuedCancellation.Token).AsTask();
            await WaitUntilAsync(
                () => harness.DecodeQueueDepth >= 1 &&
                      harness.DecodeQueueReservations >= 1 &&
                      harness.ActiveDecodes == workerCount,
                "persistent decode queued request scheduler ownership without decode credit");
            await queuedCancellation.CancelAsync();
            await EnsureRemoteCancelledAsync(queued, "queued persistent decode remote cancel");
            await WaitUntilAsync(
                () => harness.ActiveCalls == workerCount && harness.ActiveDecodes == workerCount,
                "queued cancellation resource release before worker service");
            Ensure(serverProvider.StartedCount == workerCount &&
                   harness.DecodeStartedWorkCount == workerCount,
                "queued cancellation must not start provider work");

            serverProvider.ReleaseAll();
            await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => harness.DecodeSkippedBeforeStart >= 1 &&
                      harness.DecodeQueueDepth == 0 &&
                      harness.DecodeQueueReservations == 0,
                "cancelled queued work skipped by worker");
            Ensure(serverProvider.StartedCount == workerCount &&
                   harness.DecodeStartedWorkCount == workerCount,
                "skipping the cancelled work must never execute the provider");
            await AssertResourcesReleasedAsync(harness, "queued remote cancel");
            Ensure(PersistentDecodeControlPlaneService.Invocations == workerCount,
                "only the worker-owned blocker calls may reach the service");
        }
        finally
        {
            serverProvider.ReleaseAll();
            foreach (var cancellation in blockerCancellations)
            {
                await cancellation.CancelAsync();
                cancellation.Dispose();
            }
            await Task.WhenAll(blockers.Select(ObserveTerminalAsync));
        }
    }

    [Test]
    [NotInParallel]
    public async Task RunningPersistentDecodeShouldObserveConnectionClose()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        using var cancellation = new CancellationTokenSource();
        var call = harness.Client.Get<IPersistentDecodeControlPlaneService>()
            .MeasureAsync(CreateLargePayload(0x71), cancellation.Token)
            .AsTask();

        try
        {
            await serverProvider.WaitForStartedCountAsync(1);
            await harness.StopClientAsync();
            await serverProvider.WaitForCancellationCountAsync(1);
            await EnsureConnectionClosedAsync(call, "persistent decode connection close");
            await AssertResourcesReleasedAsync(harness, "connection close");
            Ensure(PersistentDecodeControlPlaneService.Invocations == 0,
                "connection-closed decode must not invoke the service");
        }
        finally
        {
            serverProvider.ReleaseAll();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ForceStopShouldCancelRunningPersistentDecodeAndDrainExecutor()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        using var cancellation = new CancellationTokenSource();
        var call = harness.Client.Get<IPersistentDecodeControlPlaneService>()
            .MeasureAsync(CreateLargePayload(0x72), cancellation.Token)
            .AsTask();

        try
        {
            await serverProvider.WaitForStartedCountAsync(1);
            await harness.StopServerAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await serverProvider.WaitForCancellationCountAsync(1);
            await ObserveTerminalAsync(call);
            await AssertResourcesReleasedAsync(harness, "force stop");
            Ensure(harness.DecodeQueueDepth == 0,
                "force stop must leave no pending persistent decode work");
            Ensure(PersistentDecodeControlPlaneService.Invocations == 0,
                "force-stopped decode must not invoke the service");
        }
        finally
        {
            serverProvider.ReleaseAll();
        }
    }

    private static byte[] CreateLargePayload(byte value)
        => Enumerable.Repeat(value, LargePayloadBytes).ToArray();

    private static async Task AssertResourcesReleasedAsync(PersistentDecodeHarness harness, string scenario)
    {
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 &&
                  harness.ActiveDecodes == 0 &&
                  harness.RetainedCompressedBytes == 0 &&
                  harness.DecodedBytesInFlight == 0 &&
                  harness.DecodeQueueReservations == 0,
            $"{scenario} resource release");
    }

    private static async Task EnsureRemoteCancelledAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should cancel");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Cancelled)
        {
        }
    }

    private static async Task EnsureConnectionClosedAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Cancelled)
        {
        }
    }

    private static async Task ObserveTerminalAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private sealed class BlockingServerCompressionProvider(
        ISharpLinkCompressionProvider inner,
        bool initiallyReleased = false) : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new(initiallyReleased);
        private int _startedCount;
        private int _cancellationCount;

        public string WireProfile => inner.WireProfile;

        internal int StartedCount => Volatile.Read(ref _startedCount);

        internal void ReleaseAll() => _release.Set();

        internal Task WaitForStartedCountAsync(int count)
            => WaitForCounterAsync(() => StartedCount, count, "provider starts");

        internal Task WaitForCancellationCountAsync(int count)
            => WaitForCounterAsync(
                () => Volatile.Read(ref _cancellationCount),
                count,
                "provider cancellations");

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
            Interlocked.Increment(ref _startedCount);
            try
            {
                _release.Wait(cancellationToken);
                return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }

        private static async Task WaitForCounterAsync(
            Func<int> read,
            int expected,
            string scenario)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (read() < expected)
                    await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Exception($"assert failed: {scenario} did not reach {expected}");
            }
        }
    }

    private sealed class PersistentDecodeHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _clientStopped;
        private bool _serverStopped;

        private PersistentDecodeHarness(
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

        internal ISharpLinkClient Client { get; }

        internal int ActiveCalls => ReadField<int>("_globalActiveCalls");
        internal int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        internal long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        internal long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");
        internal int DecodeWorkerCount => ReadDiagnosticProperty<int>("DecodeWorkerCountForDiagnostics");
        internal int DecodeQueueDepth => ReadDiagnosticProperty<int>("DecodeQueueDepthForDiagnostics");
        internal int DecodeQueueReservations =>
            ReadDiagnosticProperty<int>("DecodeQueueReservationsForDiagnostics");
        internal int DecodeSkippedBeforeStart =>
            ReadDiagnosticProperty<int>("DecodeSkippedBeforeStartForDiagnostics");
        internal int DecodeStartedWorkCount =>
            ReadDiagnosticProperty<int>("DecodeStartedWorkCountForDiagnostics");

        internal static async Task<PersistentDecodeHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 16;
                    options.FlowControl.MaxConcurrentCallsPerServer = 16;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 8;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 32L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 128L * 1024 * 1024;
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
            return new PersistentDecodeHarness(serverCts, serverTask, server, client);
        }

        internal async ValueTask StopClientAsync()
        {
            if (_clientStopped)
                return;
            _clientStopped = true;
            await Client.StopAsync();
        }

        internal async ValueTask StopServerAsync(TimeSpan timeout)
        {
            if (_serverStopped)
                return;
            _serverStopped = true;
            await _server.StopAsync(timeout);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_clientStopped)
                await StopClientAsync();
            await _serverCts.CancelAsync();
            if (!_serverStopped)
                await StopServerAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private T ReadField<T>(string name)
        {
            var field = _server.GetType().GetField(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find server field {name}");
            return (T)field.GetValue(_server)!;
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

[RpcContract]
public interface IPersistentDecodeControlPlaneService : IService
{
    ValueTask<int> MeasureAsync(byte[] value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class PersistentDecodeControlPlaneService : IPersistentDecodeControlPlaneService
{
    private static int s_invocations;

    internal static int Invocations => Volatile.Read(ref s_invocations);

    internal static void Reset() => Volatile.Write(ref s_invocations, 0);

    public ValueTask<int> MeasureAsync(byte[] value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref s_invocations);
        return ValueTask.FromResult(value.Length);
    }
}