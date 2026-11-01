using System.Collections.Frozen;

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
        var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        List<Exception> cleanupFailures = [];
        try
        {
            await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
            Ensure(harness.DecodeAccepting, "persistent decode executor must accept work after server start");

            var serviceProxy = harness.Client.CreateProxy<IPersistentDecodeControlPlaneService>();
            await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(5));
            var service = serviceProxy.Service;
            var workerCount = harness.DecodeWorkerCount;
            var cancellations = Enumerable.Range(0, workerCount + 1)
                .Select(static _ => new CancellationTokenSource())
                .ToArray();
            List<Task> running = [];
            Task? queued = null;
            Task? stopTask = null;

            List<Exception> drainFailures = [];
            try
            {
                // Retain each started task even if a later invocation fails synchronously.
                for (var index = 0; index < workerCount; index++)
                    running.Add(service.MeasureAsync(
                        CreateLargePayload((byte)(0x80 + index)),
                        cancellations[index].Token).AsTask());
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
            catch (Exception primary) { drainFailures.Add(primary); }

            // Releasing and cancelling owners cannot skip any independently observed join.
            try { serverProvider.ReleaseAll(); }
            catch (Exception error) { drainFailures.Add(error); }
            foreach (var cancellation in cancellations)
            {
                await CaptureCleanupAsync(drainFailures, () => new ValueTask(cancellation.CancelAsync()),
                    TimeSpan.FromSeconds(5));
                try { cancellation.Dispose(); }
                catch (Exception error) { drainFailures.Add(error); }
            }
            // Join running calls concurrently, preserving the original five-second phase
            // budget while keeping every task's failure, including partial-group faults.
            var runningOutcomes = await Task.WhenAll(running.Select(async task =>
            {
                List<Exception> outcomes = [];
                await CaptureCleanupAsync(outcomes, () => new ValueTask(task), TimeSpan.FromSeconds(5));
                return outcomes;
            }));
            foreach (var outcomes in runningOutcomes)
                drainFailures.AddRange(outcomes);
            if (queued is not null)
                await CaptureCleanupAsync(drainFailures, () => new ValueTask(queued), TimeSpan.FromSeconds(5));
            if (stopTask is not null)
                await CaptureCleanupAsync(drainFailures, () => new ValueTask(stopTask), TimeSpan.FromSeconds(5));
            ThrowFailures(drainFailures);
        }
        catch (Exception primary) { cleanupFailures.Add(primary); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
        ThrowFailures(cleanupFailures);
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
        var harness = await PersistentDecodeHarness.CreateAsync(provider);
        List<Exception> cleanupFailures = [];
        try
        {
            await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
            using var cancellation = new CancellationTokenSource();
            var services = ServerRegistryTestAccessor.OverrideServices((SharpLinkServer)harness.Server);
            Task<int>? call = null;
            List<Exception>? failures = null;
            try
            {
                var originalServices = services.OriginalServices;
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
                services.Publish(installed.ToFrozenDictionary());
                var serviceProxy = harness.Client.CreateProxy<IPersistentDecodeControlPlaneService>();
                await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(5));
                call = serviceProxy.Service
                    .MeasureAsync(CreateLargePayload(0xB2), cancellation.Token).AsTask();
                await provider.WaitForStartedCountAsync(1);
                await WaitUntilAsync(() => harness.ActiveDecodes == 1, "one physical decode is blocked");
                Ensure(harness.DecodeWorkerCount > 0 && harness.DecodeStartedWorkCount == 1 &&
                       provider.StartedCount == 1 && harness.ActiveDecodes == 1,
                    "the request must actually enter one persistent decode operation");
                Ensure(original.Resolutions == 1 && original.LastResolved is { IsKnown: true } &&
                       original.Invocations == 0 && PersistentDecodeControlPlaneService.Invocations == 0,
                    "complete declared facts resolve once during cold publication, with no business invocation yet");
                var selectedFacts = original.LastResolved;
                var changed = originalServices.ToDictionary(item => item.Key, item => item.Value);
                if (remove)
                    changed.Remove(original.InterfaceHash);
                else
                    changed[original.InterfaceHash] = ServiceRegistration.CreateSingleton(
                        registration.ContractType, replacement, new PersistentDecodeControlPlaneService(), ownsService: false);
                services.Publish(changed.ToFrozenDictionary());
                provider.ReleaseAll();
                Ensure(await call.WaitAsync(TimeSpan.FromSeconds(5)) == LargePayloadBytes,
                    "the original provider/service must finish the retained request");
                Ensure(original.Resolutions == 1 && original.Invocations == 1 &&
                       ReferenceEquals(original.LastResolved, selectedFacts) && original.OriginalFactsForwarded &&
                       replacement.Resolutions == (remove ? 0 : 1) && replacement.Invocations == 0 &&
                       PersistentDecodeControlPlaneService.Invocations == 1,
                    "persistent continuation neither resolves again nor retargets registry replacement");
                Ensure(provider.StartedCount == 1 && harness.DecodeStartedWorkCount == 1,
                    "registry mutation must not restart physical decoding");
                await AssertResourcesReleasedAsync(harness, "persistent original-facts continuation");
            }
            catch (Exception primary)
            {
                (failures ??= []).Add(primary);
            }

            // Each owner is released independently. A body failure, failed terminal join and
            // failed snapshot restoration are separate observations, even for the same exception.
            try { provider.ReleaseAll(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            if (call is not null)
                await CaptureCleanupAsync(failures ??= [], () => new ValueTask(call), TimeSpan.FromSeconds(5));
            await CaptureCleanupAsync(failures ??= [], () => services.DisposeAsync(), TimeSpan.FromSeconds(5));
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 0 })
                throw new AggregateException("Persistent decode body/join/registry cleanup failures.", failures);
        }
        catch (Exception primary) { cleanupFailures.Add(primary); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
        ThrowFailures(cleanupFailures);
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
        var harness = await PersistentDecodeHarness.CreateAsync(serverProvider);
        List<Exception> cleanupFailures = [];
        Task<int>? call = null;
        (Task Operation, Exception Failure)? acceptedCallFailure = null;
        try
        {
            await WaitUntilAsync(() => harness.DecodeWorkerCount > 0, "persistent decode workers started");
            using var cancellation = new CancellationTokenSource();
            var serviceProxy = harness.Client.CreateProxy<IPersistentDecodeControlPlaneService>();
            await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(5));
            call = serviceProxy.Service
                .MeasureAsync(CreateLargePayload(0xA1), cancellation.Token)
                .AsTask();

            acceptedCallFailure = (call, await EnsureRpcFailureAsync(call, expectedCode, scenario));
            Ensure(serverProvider.StartedCount == 1,
                $"{scenario} must execute exactly once on a persistent decode worker");
            Ensure(harness.DecodeStartedWorkCount == 1,
                $"{scenario} executor start count");
            Ensure(PersistentDecodeControlPlaneService.Invocations == 0,
                $"{scenario} must fail before service invocation");
            await AssertResourcesReleasedAsync(harness, scenario);
        }
        catch (Exception primary) { cleanupFailures.Add(primary); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
        await JoinRetainedOperationAsync(cleanupFailures, call, acceptedCallFailure);
        ThrowFailures(cleanupFailures);
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

    private static async Task<SharpLinkException> EnsureRpcFailureAsync(
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
            return exception;
        }
    }

    private static async Task JoinRetainedOperationAsync(
        List<Exception> failures,
        Task? operation,
        (Task Operation, Exception Failure)? acceptedFailure = null)
    {
        if (operation is null) return;
        bool IsAccepted(Exception error)
            => acceptedFailure is { } accepted && ReferenceEquals(accepted.Operation, operation) &&
               (ReferenceEquals(accepted.Failure, error) ||
                (operation.IsCanceled && accepted.Failure is OperationCanceledException original &&
                 error is OperationCanceledException repeated && original.CancellationToken == repeated.CancellationToken));
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error)
        {
            // Only an outcome already accepted from this exact immutable task can be
            // acknowledged twice. A timeout is never an accepted logical RPC failure.
            if (error is TimeoutException)
                failures.Add(error);
            if (operation is { IsFaulted: true, Exception: { } aggregate })
            {
                foreach (var fault in aggregate.InnerExceptions)
                    if (!IsAccepted(fault)) failures.Add(fault);
            }
            else if (error is not TimeoutException && !IsAccepted(error))
                failures.Add(error);
        }
    }

    // Each awaited task is the actual operation, not a delay winner. A timeout is a
    // cleanup failure (the physical join is incomplete), never successful disposal.
    private static async Task CaptureCleanupAsync(
        List<Exception> failures, Func<ValueTask> step, TimeSpan timeout)
    {
        Task? operation = null;
        try
        {
            operation = step().AsTask();
            await operation.WaitAsync(timeout);
        }
        catch (Exception error)
        {
            // A fault racing the timeout must not erase the deadline observation.
            if (error is TimeoutException || operation is not { IsFaulted: true })
                failures.Add(error);
            if (operation is { IsFaulted: true, Exception: { } aggregate })
                failures.AddRange(aggregate.InnerExceptions);
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Body/setup and independent cleanup failures.", failures);
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
        private Task? _disposeTask;
        private Task? _serverStopTask;

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
            ISharpLinkServer? server = null;
            ISharpLinkClient? client = null;
            Task? serverTask = null;
            Task? connectTask = null;
            try
            {
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
                server = serverBuilder.Build();
                var serverToken = serverCts.Token;
                serverTask = Task.Run(
                    () => server.RunUntilStoppedAsync(serverToken).AsTask(), CancellationToken.None);
                client = SharpClientBuilder.Create().DisableRequestTimeout()
                    .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                    .UseTcp(IPAddress.Loopback.ToString(), port)
                    .UseRuntime(options => options.Compression.Providers.Add(
                        new TestCompressionProvider()))
                    .Build();
                connectTask = client.ConnectAsync().AsTask();
                await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
                return new PersistentDecodeHarness(serverCts, serverTask, server, client);
            }
            catch (Exception primary)
            {
                List<Exception> failures = [primary];
                await CaptureCleanupAsync(failures, () => client?.DisposeAsync() ?? ValueTask.CompletedTask,
                    TimeSpan.FromSeconds(5));
                await CaptureCleanupAsync(failures, () => new ValueTask(serverCts.CancelAsync()),
                    TimeSpan.FromSeconds(5));
                await CaptureCleanupAsync(failures, () => server?.DisposeAsync() ?? ValueTask.CompletedTask,
                    TimeSpan.FromSeconds(5));
                if (connectTask is not null)
                    await CaptureCleanupAsync(failures, () => new ValueTask(connectTask), TimeSpan.FromSeconds(5));
                if (serverTask is not null)
                    await CaptureCleanupAsync(failures, () => new ValueTask(serverTask), TimeSpan.FromSeconds(1));
                try { serverCts.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                ThrowFailures(failures);
                throw;
            }
        }

        internal Task BeginStopServer(TimeSpan timeout)
            => _serverStopTask ??= _server.StopAsync(timeout).AsTask();

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

        public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync()
        {
            List<Exception> failures = [];
            await CaptureCleanupAsync(failures, () => Client.StopAsync(), TimeSpan.FromSeconds(5));
            await CaptureCleanupAsync(failures, () => new ValueTask(_serverCts.CancelAsync()), TimeSpan.FromSeconds(5));
            // Rejoin a previously requested graceful stop; never turn a pending/failed
            // stop into CompletedTask merely because stop was already requested.
            await CaptureCleanupAsync(failures, () => new ValueTask(BeginStopServer(TimeSpan.Zero)),
                TimeSpan.FromSeconds(5));
            await CaptureCleanupAsync(failures, () => new ValueTask(_serverTask), TimeSpan.FromSeconds(1));
            try { _serverCts.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            ThrowFailures(failures);
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
