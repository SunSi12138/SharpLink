using System.Collections.Frozen;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class CompressionPersistentDecodeDrainAndFailureTests
{
    private const int LargePayloadBytes = 2 * 1024 * 1024;

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldClosePublicationAndDrainAlreadyQueuedDecodeWork()
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new BlockingServerCompressionProvider(
            new TestCompressionProvider());
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        Ensure(harness.DecodeAccepting, "persistent decode executor must accept work after server start");

        var service = harness.Client.Get<IPersistentDecodeControlPlaneService>();
        var workerCount = harness.DecodeWorkerCount;
        var cancellations = Enumerable.Range(0, workerCount + 1)
            .Select(static _ => new CancellationTokenSource())
            .ToArray();
        var running = Enumerable.Range(0, workerCount)
            .Select(index => service.MeasureAsync(
                    CreateLargePayload((byte)(0x80 + index)),
                    cancellations[index].Token)
                .AsTask())
            .ToArray();
        Task? queued = null;
        Task? stopTask = null;

        try
        {
            await serverProvider.WaitForStartedCountAsync(workerCount);
            queued = service.MeasureAsync(
                    CreateLargePayload(0x90),
                    cancellations[workerCount].Token)
                .AsTask();
            await WaitUntilAsync(
                () => harness.DecodeQueueDepth >= 1 &&
                      harness.DecodeQueueReservations >= 1 &&
                      harness.ActiveDecodes == workerCount,
                "decode queued before graceful drain without queued decode credit");

            stopTask = harness.BeginStopServer(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => !harness.DecodeAccepting,
                "graceful stop decode publication boundary");

            Ensure(!stopTask.IsCompleted,
                "graceful stop must remain joined to running and queued persistent decodes");
            Ensure(serverProvider.CancellationCount == 0,
                "graceful drain must not force-cancel provider work before its timeout");

            serverProvider.ReleaseAll();
            await Task.WhenAll(running.Append(queued)).WaitAsync(TimeSpan.FromSeconds(5));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(serverProvider.StartedCount == workerCount + 1,
                "work published before the drain boundary must still receive worker service");
            Ensure(serverProvider.CancellationCount == 0,
                "successful graceful drain must not cancel persistent decode providers");
            Ensure(harness.DecodeQueueDepth == 0,
                "graceful stop must drain the persistent decode queue");
            await AssertResourcesReleasedAsync(harness, "graceful persistent decode stop");
        }
        finally
        {
            serverProvider.ReleaseAll();
            foreach (var cancellation in cancellations)
            {
                await cancellation.CancelAsync();
                cancellation.Dispose();
            }
            await Task.WhenAll(running.Select(ObserveTerminalAsync));
            if (queued is not null)
                await ObserveTerminalAsync(queued);
            if (stopTask is not null)
                await ObserveTerminalAsync(stopTask);
        }
    }

    [Test]
    [NotInParallel]
    public Task PersistentDecodeDataLossShouldReleaseAllRequestResources()
        => RunProviderFailureCaseAsync(
            static () => new InvalidDataException("synthetic corrupt compressed payload"),
            SharpLinkErrorCode.DataLoss,
            "persistent D DataLoss");

    [Test]
    [NotInParallel]
    public Task PersistentDecodeInternalShouldReleaseAllRequestResources()
        => RunProviderFailureCaseAsync(
            static () => new InvalidOperationException("synthetic provider failure"),
            SharpLinkErrorCode.Internal,
            "persistent D Internal");

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PersistentDecodeContinuationShouldRetainOriginalFactsAfterRegistryChange(bool remove)
    {
        PersistentDecodeControlPlaneService.Reset();
        var provider = new BlockingServerCompressionProvider(new TestCompressionProvider());
        await using var harness = await PersistentDecodeHarness.CreateAsync(provider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
            "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!;
        var originalServices = registry.ServicesStorage;
        var registration = originalServices.Values.Single(item =>
            item.ContractType == typeof(IPersistentDecodeControlPlaneService));
        Ensure(!registration.HasModule,
            "this facts continuation probe uses a static registration, not dynamic retirement");
        Ensure(registration.TryGetStaticSingleton(out var serviceInstance), "original singleton is available");
        var original = new CountingPersistentFactsStub(registration.Stub);
        var replacement = new CountingPersistentFactsStub(registration.Stub);
        var installed = originalServices.ToDictionary(item => item.Key, item => item.Value);
        installed[original.InterfaceHash] = ServiceRegistration.CreateSingleton(
            registration.ContractType, original, serviceInstance, ownsService: false);
        using var cancellation = new CancellationTokenSource();
        Task<int>? call = null;
        try
        {
            Volatile.Write(ref registry.ServicesStorage, installed.ToFrozenDictionary());
            call = harness.Client.Get<IPersistentDecodeControlPlaneService>()
                .MeasureAsync(CreateLargePayload(0xB2), cancellation.Token).AsTask();
            await provider.WaitForStartedCountAsync(1);
            await WaitUntilAsync(() => harness.ActiveDecodes == 1, "one physical decode is blocked");
            Ensure(harness.DecodeWorkerCount > 0 && harness.DecodeStartedWorkCount == 1 &&
                   provider.StartedCount == 1 && harness.ActiveDecodes == 1,
                "the request must actually enter one persistent decode operation");
            Ensure(original.Resolutions == 1 && original.LastResolved is { IsKnown: true } &&
                   original.Invocations == 0 && PersistentDecodeControlPlaneService.Invocations == 0,
                "facts resolve once before decoding, with no business invocation yet");
            var selectedFacts = original.LastResolved;
            var changed = originalServices.ToDictionary(item => item.Key, item => item.Value);
            if (remove)
                changed.Remove(original.InterfaceHash);
            else
                changed[original.InterfaceHash] = ServiceRegistration.CreateSingleton(
                    registration.ContractType, replacement, new PersistentDecodeControlPlaneService(), ownsService: false);
            Volatile.Write(ref registry.ServicesStorage, changed.ToFrozenDictionary());
            provider.ReleaseAll();
            Ensure(await call.WaitAsync(TimeSpan.FromSeconds(5)) == LargePayloadBytes,
                "the original provider/service must finish the retained request");
            Ensure(original.Resolutions == 1 && original.Invocations == 1 &&
                   ReferenceEquals(original.LastResolved, selectedFacts) && original.OriginalFactsForwarded &&
                   replacement.Resolutions == 0 && replacement.Invocations == 0 &&
                   PersistentDecodeControlPlaneService.Invocations == 1,
                "persistent continuation neither resolves again nor retargets registry replacement");
            Ensure(provider.StartedCount == 1 && harness.DecodeStartedWorkCount == 1,
                "registry mutation must not restart physical decoding");
            await AssertResourcesReleasedAsync(harness, "persistent original-facts continuation");
        }
        finally
        {
            provider.ReleaseAll();
            try
            {
                if (call is not null)
                    await ObserveTerminalAsync(call);
            }
            finally
            {
                Volatile.Write(ref registry.ServicesStorage, originalServices);
            }
        }
    }

    private sealed class CountingPersistentFactsStub(IRpcStub inner) : IRpcStub
    {
        internal int Resolutions;
        internal int Invocations;
        internal RpcMethodFacts? LastResolved;
        internal bool OriginalFactsForwarded = true;
        public long InterfaceHash => inner.InterfaceHash;
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        {
            Interlocked.Increment(ref Resolutions);
            return LastResolved = inner.ResolveMethodFacts(methodHash);
        }
        private void Observe(RpcMethodFacts method)
        {
            Interlocked.Increment(ref Invocations);
            OriginalFactsForwarded &= ReferenceEquals(LastResolved, method);
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args)
        {
            Observe(method);
            return inner.InvokeNoReturnAsync(service, bridge, method, requestId, args);
        }
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
        {
            Observe(method);
            return inner.InvokeNoReturnCancellableAsync(service, bridge, method, requestId, args, cancellationToken);
        }
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
        {
            Observe(method);
            return inner.InvokeAsync(service, bridge, method, requestId, args, output);
        }
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken)
        {
            Observe(method);
            return inner.InvokeCancellableAsync(service, bridge, method, requestId, args, output, cancellationToken);
        }
    }

    private static async Task RunProviderFailureCaseAsync(
        Func<Exception> failureFactory,
        SharpLinkErrorCode expectedCode,
        string scenario)
    {
        PersistentDecodeControlPlaneService.Reset();
        var serverProvider = new ThrowingServerCompressionProvider(
            new TestCompressionProvider(),
            failureFactory);
        await using var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
        using var cancellation = new CancellationTokenSource();
        var call = harness.Client.Get<IPersistentDecodeControlPlaneService>()
            .MeasureAsync(CreateLargePayload(0xA1), cancellation.Token)
            .AsTask();

        await EnsureRpcFailureAsync(call, expectedCode, scenario);
        Ensure(serverProvider.StartedCount == 1,
            $"{scenario} must execute exactly once on a persistent decode worker");
        Ensure(harness.DecodeStartedWorkCount == 1,
            $"{scenario} executor start count");
        Ensure(PersistentDecodeControlPlaneService.Invocations == 0,
            $"{scenario} must fail before service invocation");
        await AssertResourcesReleasedAsync(harness, scenario);
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
                  harness.DecodeQueueDepth == 0 &&
                  harness.DecodeQueueReservations == 0,
            $"{scenario} resource release");
    }

    private static async Task EnsureRpcFailureAsync(
        Task task,
        SharpLinkErrorCode expectedCode,
        string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {scenario} should fail with {expectedCode}");
        }
        catch (SharpLinkException exception) when (exception.Code == expectedCode)
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
        ISharpLinkCompressionProvider inner) : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new();
        private int _startedCount;
        private int _cancellationCount;

        public string WireProfile => inner.WireProfile;

        internal int StartedCount => Volatile.Read(ref _startedCount);

        internal int CancellationCount => Volatile.Read(ref _cancellationCount);

        internal void ReleaseAll() => _release.Set();

        internal Task WaitForStartedCountAsync(int count)
            => WaitForCounterAsync(() => StartedCount, count, "provider starts");

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
            Interlocked.Increment(ref _startedCount);
            try
            {
                _release.Wait(cancellationToken);
                inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }
    }

    private sealed class ThrowingServerCompressionProvider(
        ISharpLinkCompressionProvider inner,
        Func<Exception> failureFactory) : ISharpLinkCompressionProvider
    {
        private int _startedCount;

        public string WireProfile => inner.WireProfile;

        internal int StartedCount => Volatile.Read(ref _startedCount);

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
            Interlocked.Increment(ref _startedCount);
            throw failureFactory();
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
        internal ISharpLinkServer Server => _server;

        internal int ActiveCalls => ServerCallAdmissionDiagnostics.ActiveCallCount(_server);
        internal int ActiveDecodes => ReadDiagnosticProperty<int>("ActiveDecodeCountForDiagnostics");
        internal long RetainedCompressedBytes =>
            ReadDiagnosticProperty<long>("RetainedCompressedBytesForDiagnostics");
        internal long DecodedBytesInFlight =>
            ReadDiagnosticProperty<long>("DecodedBytesInFlightForDiagnostics");
        internal int DecodeWorkerCount => ReadDiagnosticProperty<int>("DecodeWorkerCountForDiagnostics");
        internal int DecodeQueueDepth => ReadDiagnosticProperty<int>("DecodeQueueDepthForDiagnostics");
        internal int DecodeQueueReservations =>
            ReadDiagnosticProperty<int>("DecodeQueueReservationsForDiagnostics");
        internal int DecodeStartedWorkCount =>
            ReadDiagnosticProperty<int>("DecodeStartedWorkCountForDiagnostics");
        internal bool DecodeAccepting => ReadDiagnosticProperty<bool>("DecodeAcceptingForDiagnostics");

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
                .UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunUntilStoppedAsync(serverCts.Token);
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

            var client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRuntime(options => options.Compression.Providers.Add(
                    new TestCompressionProvider()))
                .Build();
            await client.ConnectAsync();
            return new PersistentDecodeHarness(serverCts, serverTask, server, client);
        }

        internal Task BeginStopServer(TimeSpan timeout)
        {
            if (_serverStopped)
                return Task.CompletedTask;
            _serverStopped = true;
            return _server.StopAsync(timeout).AsTask();
        }

        internal static async Task WaitForCounterAsync(
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

        public async ValueTask DisposeAsync()
        {
            if (!_clientStopped)
            {
                _clientStopped = true;
                try
                {
                    await Client.StopAsync();
                }
                catch (Exception)
                {
                }
            }
            await _serverCts.CancelAsync();
            if (!_serverStopped)
            {
                _serverStopped = true;
                await _server.StopAsync(TimeSpan.Zero);
            }
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

    private static Task WaitForCounterAsync(
        Func<int> read,
        int expected,
        string scenario)
        => PersistentDecodeHarness.WaitForCounterAsync(read, expected, scenario);
}
