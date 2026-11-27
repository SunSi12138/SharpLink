using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class ClientStreamingResultStressTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ServerInterceptorReplacementClientStreamingResultShouldRemainStable()
    {
        const int iterations = 10;
        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            var harness = await AwaitPhaseAsync(Harness.CreateAsync(), iteration, "create harness");
            var log = new ConcurrentQueue<string>();
            var a = new AwaitingServerInterceptor("A", log);
            var b = new GatedNextServerInterceptor("B", log);
            var c = new AwaitingServerInterceptor("C", log);
            try
            {
                var service = await harness.Client.PrepareTestServiceAsync<IInterceptorTestService>();
                var x = new AwaitingServerInterceptor("X", log);
                var y = new AwaitingServerInterceptor("Y", log);
                var z = new AwaitingServerInterceptor("Z", log);
                harness.Server.ReplaceInterceptors([a, b, c]);

                var input = new ObservedInput<int>(SingleValue());
                var call = service.SumStreamAsync(input, CancellationToken.None).AsTask();
                await AwaitPhaseAsync(b.Entered, iteration, "server interceptor entry");
                await AwaitPhaseAsync(input.Started, iteration, "client input starts");
                if (call.IsCompleted)
                    throw new InvalidOperationException("A gated interceptor must withhold the result without blocking the sender.");

                harness.Server.ReplaceInterceptors([x, y, z]);
                b.Release();
                if (await AwaitPhaseAsync(call, iteration, "captured client-streaming result") != 10 || input.Enumerators != 1)
                    throw new InvalidOperationException("The accepted original source must be read once and return 10.");
                await AwaitPhaseAsync(Task.WhenAll(a.Completed, b.Completed, c.Completed), iteration, "original interceptor unwind");

                if (await AwaitPhaseAsync(service.SumStreamAsync(SingleValue(), CancellationToken.None).AsTask(),
                        iteration, "replacement client-streaming result") != 10)
                    throw new InvalidOperationException("The next call must succeed through the new interceptor generation.");
                await AwaitPhaseAsync(Task.WhenAll(x.Completed, y.Completed, z.Completed), iteration, "replacement interceptor unwind");

                if (!log.SequenceEqual(["A:before", "B:before", "C:before", "C:after", "B:after", "A:after",
                        "X:before", "Y:before", "Z:before", "Z:after", "Y:after", "X:after"]))
                    throw new InvalidOperationException($"The two calls must unwind only their own snapshots: {string.Join(", ", log)}");
            }
            finally
            {
                b.Release();
                await AwaitPhaseAsync(harness.DisposeAsync().AsTask(), iteration, "harness disposal");
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task CompressedClientStreamingInputShouldStayRawUntilHandlerEntry()
    {
        var decoder = new CountingServerCompressionProvider();
        var harness = await Harness.CreateAsync(serverCompressionProvider: decoder);
        var gate = new GatedNextServerInterceptor("compressed", new ConcurrentQueue<string>());
        var rawBuffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var compression = await harness.Client.PrepareTestServiceAsync<ICompressionService>();
            harness.Server.ReplaceInterceptors([gate]);
            var payload = Enumerable.Repeat((byte)0x41, 4096).ToArray();
            var input = new ObservedInput<byte[]>(SinglePayload(payload));
            var decodedBeforeInput = decoder.Decodes;
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests = (_, _, compressed) =>
            {
                if (compressed)
                    rawBuffered.TrySetResult();
            };
            var call = compression.UploadBytesAsync(input).AsTask();

            await gate.Entered.WaitAsync(PhaseTimeout);
            await rawBuffered.Task.WaitAsync(PhaseTimeout);
            if (call.IsCompleted || input.Enumerators != 1 || decoder.Decodes != decodedBeforeInput)
                throw new InvalidOperationException("Compressed input may arrive, but must not be decoded while the interceptor is held.");

            gate.Release();
            if (await call.WaitAsync(PhaseTimeout) != payload.Length ||
                input.Enumerators != 1 || decoder.Decodes != decodedBeforeInput + 1)
                throw new InvalidOperationException("After handler entry the typed stream must decode exactly one compressed item.");
        }
        finally
        {
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests = null;
            gate.Release();
            await harness.DisposeAsync().AsTask().WaitAsync(PhaseTimeout);
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamShouldSwitchFromQueuedToActiveBudget()
    {
        InterceptorTestService.ResetDelayedCall();
        using var metrics = new LifecycleMetricProbe(LifecycleMetricProbe.AdmissionQueuedCalls);
        var gate = new ClientStreamingGateServerInterceptor();
        var releaseRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = await Harness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 2048;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
            }));
        try
        {
            var unary = await harness.Client.PrepareTestServiceAsync<IInterceptorTestService>();
            var compression = await harness.Client.PrepareTestServiceAsync<ICompressionService>();
            harness.Server.ReplaceInterceptors([gate]);
            var server = (SharpLinkServer)harness.Server;
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            var permitOwner = unary.DelayedAsync().AsTask();
            await InterceptorTestService.DelayedCallStarted.WaitAsync(PhaseTimeout);

            const int firstItemSize = 64;
            const int remainingItemCount = 16;
            const int remainingItemSize = 512;
            var input = new ObservedInput<byte[]>(Payloads(firstItemSize, remainingItemCount, remainingItemSize, releaseRest.Task));
            var call = compression.UploadBytesAsync(input).AsTask();

            await metrics.WaitForValueAsync(LifecycleMetricProbe.AdmissionQueuedCalls, 1, "upload head is queued");
            using var guard = new CancellationTokenSource(PhaseTimeout);
            while (server.PreAdmissionStreamBytesForDiagnostics == 0)
                await Task.Delay(10, guard.Token);
            var retained = server.PreAdmissionStreamBytesForDiagnostics;
            if (call.IsCompleted || input.Enumerators != 1 || kernel.QueuedBytes <= 0)
                throw new InvalidOperationException("The queued head and first item must be separately accounted.");

            InterceptorTestService.ReleaseDelayedCall();
            if (await permitOwner.WaitAsync(PhaseTimeout) != 42)
                throw new InvalidOperationException("The permit owner returned an unexpected result.");

            await gate.Entered.WaitAsync(PhaseTimeout);
            if (call.IsCompleted || input.Enumerators != 1 || kernel.QueuedBytes != 0 || kernel.QueuedCalls != 0 ||
                server.PreAdmissionStreamBytesForDiagnostics != retained)
                throw new InvalidOperationException("Activation releases the head charge and keeps the original item owner.");

            releaseRest.TrySetResult();
            gate.Release();
            var expected = firstItemSize + remainingItemCount * remainingItemSize;
            if (await call.WaitAsync(PhaseTimeout) != expected || input.Enumerators != 1 ||
                server.PreAdmissionStreamBytesForDiagnostics != 0)
                throw new InvalidOperationException("The accepted stream must complete once, beyond the old queued-byte budget.");
        }
        finally
        {
            releaseRest.TrySetResult();
            gate.Release();
            InterceptorTestService.ReleaseDelayedCall();
            await harness.DisposeAsync().AsTask().WaitAsync(PhaseTimeout);
        }
    }


    private static async Task<T> AwaitPhaseAsync<T>(Task<T> task, int iteration, string phase)
    {
        try
        {
            return await task.WaitAsync(PhaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"iteration {iteration}: timed out during {phase}", exception);
        }
    }

    private static async Task AwaitPhaseAsync(Task task, int iteration, string phase)
    {
        try
        {
            await task.WaitAsync(PhaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"iteration {iteration}: timed out during {phase}", exception);
        }
    }

    private static async IAsyncEnumerable<int> SingleValue()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return 10;
    }

    private static async IAsyncEnumerable<byte[]> SinglePayload(byte[] payload)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return payload;
    }

    private static async IAsyncEnumerable<byte[]> Payloads(int firstSize, int remainingCount, int remainingSize,
        Task releaseRest, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return Enumerable.Repeat((byte)0x31, firstSize).ToArray();
        await releaseRest.WaitAsync(ct);
        var payload = Enumerable.Repeat((byte)0x32, remainingSize).ToArray();
        for (var index = 0; index < remainingCount; index++)
        {
            yield return payload;
            await Task.Yield();
        }
    }

    private sealed class ObservedInput<T>(IAsyncEnumerable<T> source) : IAsyncEnumerable<T>
    {
        private int _enumerators;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Enumerators => Volatile.Read(ref _enumerators);
        internal Task Started => _started.Task;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enumerators);
            _started.TrySetResult();
            return source.GetAsyncEnumerator(cancellationToken);
        }
    }

    private sealed class CountingServerCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private int _decodes;

        public string WireProfile => _inner.WireProfile;
        internal int Decodes => Volatile.Read(ref _decodes);

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decodes);
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class AwaitingServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class GatedNextServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public Task Completed => _completed.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class ClientStreamingGateServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.Kind == RpcMethodKind.ClientStreaming)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
        }

        public static async Task<Harness> CreateAsync(
            ISharpLinkCompressionProvider? serverCompressionProvider = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (serverCompressionProvider is not null)
                serverBuilder.UseRuntime(options => options.Compression.Providers.Add(serverCompressionProvider))
                    .UseResponseCompressionPolicy(CreateCompressionPolicy());
            serverConfigure?.Invoke(serverBuilder);

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(
                () => server.RunUntilStoppedAsync(cts.Token).AsTask(),
                CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (serverCompressionProvider is not null)
                clientBuilder.UseRuntime(ConfigureCompression).UseRequestCompressionPolicy(CreateCompressionPolicy());

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        private static SharpLinkCompressionSendPolicy CreateCompressionPolicy()
            => new()
            {
                MinimumPayloadBytes = 1,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0
            };

        private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        {
            options.Compression.Providers.Add(new TestCompressionProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
