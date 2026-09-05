namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeReviewTests
{
    private const int LargePayloadBytes = 2 * 1024 * 1024;
    private const int ProductionQueueCapacityWithOneWorker = 32;

    [Test]
    [NotInParallel]
    public async Task FullPersistentQueueShouldNotPreAcquireDecodeOrDecodedByteBudgets()
    {
        PersistentDecodeReviewService.Reset();
        var serverProvider = new BlockingReviewCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ReviewHarness.CreateAsync(
            serverProvider,
            maxConcurrentCalls: 64,
            maxConcurrentDecodes: 1,
            maxDecodedBytes: 128L * 1024 * 1024);
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "single persistent decode worker started");
        var service = harness.Client.Get<IPersistentDecodeReviewService>();
        using var cancellation = new CancellationTokenSource();
        var payload = Enumerable.Repeat((byte)0x2a, LargePayloadBytes).ToArray();

        var running = service.MeasureAsync(payload, cancellation.Token).AsTask();
        await serverProvider.WaitForStartedCountAsync(1);
        Ensure(harness.ActiveDecodes == 1, "running provider owns the only decode credit");

        var queued = Enumerable.Range(0, ProductionQueueCapacityWithOneWorker)
            .Select(_ => service.MeasureAsync(payload, cancellation.Token).AsTask())
            .ToArray();
        await WaitUntilAsync(
            () => harness.DecodeQueueDepth == ProductionQueueCapacityWithOneWorker &&
                  harness.DecodeQueueReservations == ProductionQueueCapacityWithOneWorker,
            "production persistent decode queue filled");

        Ensure(harness.ActiveDecodes == 1,
            "queued D work must not consume provider decode concurrency");
        var decodedBytesBeforeRejected = harness.DecodedBytesInFlight;
        Ensure(decodedBytesBeforeRejected > 0 && decodedBytesBeforeRejected < 4L * 1024 * 1024,
            "only the running D work may own decoded-byte budget");
        var retainedBytesBeforeRejected = harness.RetainedCompressedBytes;

        var rejected = service.MeasureAsync(payload, cancellation.Token).AsTask();
        await EnsureResourceExhaustedAsync(rejected, "full persistent decode queue");

        Ensure(harness.ActiveDecodes == 1,
            "queue-full rejection must not acquire an additional decode credit");
        Ensure(harness.DecodedBytesInFlight == decodedBytesBeforeRejected,
            "queue-full rejection must not reserve decoded-byte budget");
        Ensure(harness.RetainedCompressedBytes == retainedBytesBeforeRejected,
            "queue-full rejection must happen before D-specific retained-byte ownership");
        Ensure(harness.DecodeQueueReservations == ProductionQueueCapacityWithOneWorker,
            "queue-full rejection must not perturb accepted scheduler reservations");
        Ensure(serverProvider.StartedCount == 1,
            "queue-full rejection must not execute provider code");

        serverProvider.ReleaseAll();
        await Task.WhenAll(queued.Prepend(running)).WaitAsync(TimeSpan.FromSeconds(10));
        await AssertResourcesReleasedAsync(harness, "full queue drain");
        Ensure(PersistentDecodeReviewService.CancellableInvocations ==
               ProductionQueueCapacityWithOneWorker + 1,
            "only scheduler-admitted requests may invoke the service");
    }

    [Test]
    [NotInParallel]
    public async Task LargeNonCancellableHandlerRequestShouldStillUsePersistentDecodeAndHonorDeadlineBeforeActivation()
    {
        PersistentDecodeReviewService.Reset();
        var serverProvider = new BlockingReviewCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli());
        await using var harness = await ReviewHarness.CreateAsync(
            serverProvider,
            maxConcurrentCalls: 8,
            maxConcurrentDecodes: 1,
            clientRequestTimeout: TimeSpan.FromMilliseconds(750));
        await WaitUntilAsync(() => harness.DecodeWorkerCount == 1, "persistent decode worker started");
        var service = harness.Client.Get<IPersistentDecodeReviewService>();
        var payload = Enumerable.Repeat((byte)0x39, LargePayloadBytes).ToArray();

        var call = service.MeasureNonCancellableAsync(payload).AsTask();
        try
        {
            await serverProvider.WaitForStartedCountAsync(1);
            Ensure(harness.DecodeStartedWorkCount == 1,
                "large NonCancellable handler request must route through D");
            await serverProvider.WaitForCancellationCountAsync(1);
            await EnsureDeadlineOrCancellationAsync(call, "NonCancellable pre-activation deadline");
            await AssertResourcesReleasedAsync(harness, "NonCancellable deadline");
            Ensure(PersistentDecodeReviewService.NonCancellableInvocations == 0,
                "deadline during D must prevent NonCancellable handler activation");
        }
        finally
        {
            serverProvider.ReleaseAll();
        }
    }

    [Test]
    [NotInParallel]
    public async Task LargeCompressedInputWithSmallDeclaredOutputShouldOffloadBeforeRequestLoopCancel()
    {
        var provider = new BlockingRawInputCompressionProvider();
        await using var harness = await RawInputHarness.CreateAsync(provider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "raw-input persistent decode worker started");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, harness.Port);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        const ulong requestId = 41;
        const int declaredDecodedArgumentsBytes = 64 * 1024;
        const int compressedBodyBytes = 2 * 1024 * 1024;

        using var frames = new PooledByteBufferWriter();
        var limits = new SharpLinkProtocolOptions();
        var handshake = ProtocolV2FrameWriter.BeginFrame(
            frames,
            ProtocolV2FrameType.HandshakeRequest,
            ProtocolV2FrameFlags.None,
            0);
        ProtocolV2PayloadCodec.WriteHandshakeRequest(
            frames,
            new ProtocolV2HandshakeRequest(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.Compression,
                ProtocolV2Capabilities.Compression,
                SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                1024 * 1024,
                16 * 1024 * 1024,
                ReadOnlyMemory<byte>.Empty,
                new[] { provider.WireProfile }),
            limits);
        ProtocolV2FrameWriter.EndFrame(frames, handshake);

        var request = ProtocolV2FrameWriter.BeginFrame(
            frames,
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.Cancellable,
            requestId);
        Span<byte> requestPrefix = stackalloc byte[ProtocolV2Constants.RequestPrefixBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            requestPrefix,
            harness.InterfaceHash);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            requestPrefix[sizeof(long)..],
            1L);
        frames.Write(requestPrefix);
        Span<byte> originalLength = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            originalLength,
            declaredDecodedArgumentsBytes);
        frames.Write(originalLength);
        frames.Write(new byte[compressedBodyBytes]);
        ProtocolV2FrameWriter.EndFrame(frames, request);

        await stream.WriteAsync(frames.WrittenMemory);
        await stream.FlushAsync();
        await provider.WaitForStartedCountAsync(1);
        Ensure(harness.DecodeStartedWorkCount == 1,
            "large compressed input must route to D even when declared output is below 1 MiB");

        using var cancel = new PooledByteBufferWriter();
        ProtocolV2FrameWriter.WriteEmptyFrame(
            cancel,
            ProtocolV2FrameType.Cancel,
            ProtocolV2FrameFlags.None,
            requestId);
        await stream.WriteAsync(cancel.WrittenMemory);
        await stream.FlushAsync();

        try
        {
            await provider.WaitForCancellationCountAsync(1);
            await AssertResourcesReleasedAsync(harness, "large compressed-input cancel");
            Ensure(provider.StartedCount == 1,
                "hostile large compressed input should execute provider exactly once on D");
        }
        finally
        {
            provider.ReleaseAll();
        }
    }

    private static async Task EnsureResourceExhaustedAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should reject");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
            Ensure(exception.Message.Contains("server_decode_queue", StringComparison.Ordinal),
                $"{scenario} must preserve the persistent decode queue exhaustion reason");
        }
    }

    private static async Task EnsureDeadlineOrCancellationAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should terminate");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DeadlineExceeded or SharpLinkErrorCode.Cancelled)
        {
        }
    }

    private static async Task AssertResourcesReleasedAsync(IReviewDiagnostics harness, string scenario)
    {
        await WaitUntilAsync(
            () => harness.ActiveCalls == 0 &&
                  harness.ActiveDecodes == 0 &&
                  harness.RetainedCompressedBytes == 0 &&
                  harness.DecodedBytesInFlight == 0 &&
                  harness.DecodeQueueDepth == 0 &&
                  harness.DecodeQueueReservations == 0,
            $"{scenario} resource release");
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

    private interface IReviewDiagnostics
    {
        int ActiveCalls { get; }
        int ActiveDecodes { get; }
        long RetainedCompressedBytes { get; }
        long DecodedBytesInFlight { get; }
        int DecodeQueueDepth { get; }
        int DecodeQueueReservations { get; }
    }

    private abstract class ReviewDiagnosticsBase(ISharpLinkServer server) : IReviewDiagnostics
    {
        protected ISharpLinkServer Server { get; } = server;

        public int ActiveCalls => ServerCallAdmissionDiagnostics.ActiveCallCount(Server);
        public int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        public long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        public long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");
        public int DecodeQueueDepth => ReadDiagnosticProperty<int>("DecodeQueueDepthForDiagnostics");
        public int DecodeQueueReservations =>
            ReadDiagnosticProperty<int>("DecodeQueueReservationsForDiagnostics");
        internal int DecodeWorkerCount => ReadDiagnosticProperty<int>("DecodeWorkerCountForDiagnostics");
        internal int DecodeStartedWorkCount =>
            ReadDiagnosticProperty<int>("DecodeStartedWorkCountForDiagnostics");

        protected T ReadDiagnosticProperty<T>(string name)
        {
            var property = Server.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception($"cannot find server diagnostic property {name}");
            return (T)property.GetValue(Server)!;
        }
    }

    private sealed class ReviewHarness : ReviewDiagnosticsBase, IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private bool _stopped;

        private ReviewHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
            : base(server)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Client = client;
        }

        internal ISharpLinkClient Client { get; }

        internal static async Task<ReviewHarness> CreateAsync(
            ISharpLinkCompressionProvider serverProvider,
            int maxConcurrentCalls,
            int maxConcurrentDecodes,
            long maxDecodedBytes = 128L * 1024 * 1024,
            TimeSpan? clientRequestTimeout = null)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = maxConcurrentCalls;
                    options.FlowControl.MaxConcurrentCallsPerServer = maxConcurrentCalls;
                    options.FlowControl.MaxConcurrentDecodesPerServer = maxConcurrentDecodes;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 32L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = maxDecodedBytes;
                    options.Compression.Providers.Add(serverProvider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = RunServerAsync(server, serverCts.Token);

            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    SharpLinkCompressionProviders.CreateBrotli()));
            if (clientRequestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);
            var client = clientBuilder.Build();
            await client.ConnectAsync();
            return new ReviewHarness(serverCts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            try
            {
                await Client.StopAsync();
            }
            finally
            {
                await _serverCts.CancelAsync();
                await Server.StopAsync(TimeSpan.Zero);
                await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
                _serverCts.Dispose();
            }
        }
    }

    private sealed class RawInputHarness : ReviewDiagnosticsBase, IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private bool _stopped;

        private RawInputHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            int port,
            long interfaceHash)
            : base(server)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Port = port;
            InterfaceHash = interfaceHash;
        }

        internal int Port { get; }
        internal long InterfaceHash { get; }

        internal static Task<RawInputHarness> CreateAsync(ISharpLinkCompressionProvider provider)
        {
            var serverCts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseRuntime(options =>
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 8;
                    options.FlowControl.MaxConcurrentCallsPerServer = 8;
                    options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                    options.FlowControl.MaxRetainedCompressedBytesPerServer = 16L * 1024 * 1024;
                    options.FlowControl.MaxDecodedBytesInFlightPerServer = 8L * 1024 * 1024;
                    options.Compression.Providers.Add(provider);
                })
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var interfaceHash = ReadAnyInterfaceHash(server);
            var serverTask = RunServerAsync(server, serverCts.Token);
            return Task.FromResult(new RawInputHarness(
                serverCts,
                serverTask,
                server,
                port,
                interfaceHash));
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            await _serverCts.CancelAsync();
            await Server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }

        private static long ReadAnyInterfaceHash(ISharpLinkServer server)
        {
            foreach (var key in ServerRegistryTestAccessor.Services((SharpLinkServer)server).Keys)
                return key;
            throw new Exception("server has no registered service hash");
        }
    }

    private static Task RunServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellationToken);
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

    private sealed class BlockingReviewCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new();
        private int _startedCount;
        private int _cancellationCount;

        public string WireProfile => inner.WireProfile;
        internal int StartedCount => Volatile.Read(ref _startedCount);
        internal void ReleaseAll() => _release.Set();
        internal Task WaitForStartedCountAsync(int count)
            => WaitForCounterAsync(() => StartedCount, count, "review provider starts");
        internal Task WaitForCancellationCountAsync(int count)
            => WaitForCounterAsync(
                () => Volatile.Read(ref _cancellationCount),
                count,
                "review provider cancellations");

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
    }

    private sealed class BlockingRawInputCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new();
        private int _startedCount;
        private int _cancellationCount;

        public string WireProfile => "review-input-cost";
        internal int StartedCount => Volatile.Read(ref _startedCount);
        internal void ReleaseAll() => _release.Set();
        internal Task WaitForStartedCountAsync(int count)
            => WaitForCounterAsync(() => StartedCount, count, "raw-input provider starts");
        internal Task WaitForCancellationCountAsync(int count)
            => WaitForCounterAsync(
                () => Volatile.Read(ref _cancellationCount),
                count,
                "raw-input provider cancellations");

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
                throw new InvalidDataException("raw input probe was released without cancellation");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
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

[RpcContract]
public interface IPersistentDecodeReviewService : IService
{
    ValueTask<int> MeasureAsync(byte[] value, CancellationToken cancellationToken);

    [NonCancellable]
    ValueTask<int> MeasureNonCancellableAsync(byte[] value);
}

[RpcService]
public sealed class PersistentDecodeReviewService : IPersistentDecodeReviewService
{
    private static int s_cancellableInvocations;
    private static int s_nonCancellableInvocations;

    internal static int CancellableInvocations => Volatile.Read(ref s_cancellableInvocations);
    internal static int NonCancellableInvocations => Volatile.Read(ref s_nonCancellableInvocations);

    internal static void Reset()
    {
        Volatile.Write(ref s_cancellableInvocations, 0);
        Volatile.Write(ref s_nonCancellableInvocations, 0);
    }

    public ValueTask<int> MeasureAsync(byte[] value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref s_cancellableInvocations);
        return ValueTask.FromResult(value.Length);
    }

    public ValueTask<int> MeasureNonCancellableAsync(byte[] value)
    {
        Interlocked.Increment(ref s_nonCancellableInvocations);
        return ValueTask.FromResult(value.Length);
    }
}
