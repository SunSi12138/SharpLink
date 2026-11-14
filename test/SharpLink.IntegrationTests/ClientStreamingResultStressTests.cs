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
            var buffered = CreateBufferedObservation();
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests =
                (requestId, streamId, compressed) =>
                    buffered.TrySetResult(new BufferedObservation(requestId, streamId, compressed));

            var harness = await AwaitPhaseAsync(Harness.CreateAsync(), iteration, "create harness");
            var log = new ConcurrentQueue<string>();
            var a = new AwaitingServerInterceptor("A", log);
            var b = new GatedNextServerInterceptor("B", log);
            var c = new AwaitingServerInterceptor("C", log);
            var producer = new ProducerProbe();
            try
            {
                var x = new AwaitingServerInterceptor("X", log);
                var y = new AwaitingServerInterceptor("Y", log);
                var z = new AwaitingServerInterceptor("Z", log);
                harness.Server.ReplaceInterceptors([a, b, c]);

                var call = harness.Service.SumStreamAsync(SingleValue(producer), CancellationToken.None).AsTask();
                await AwaitPhaseAsync(b.Entered, iteration, "server interceptor entry");
                if (call.IsCompleted)
                    throw new Exception($"iteration {iteration}: invocation completed before gated next");

                if (producer.Started.IsCompleted || buffered.Task.IsCompleted)
                    throw new Exception($"iteration {iteration}: producer ran before interceptor acceptance");

                // RequestAccepted is published only when this captured interceptor chain reaches
                // its business terminal. Replacing the global chain cannot replace this call's chain.
                harness.Server.ReplaceInterceptors([x, y, z]);
                b.Release();

                var result = await AwaitPhaseAsync(call, iteration, "client-streaming result");
                if (result != 10)
                    throw new Exception($"iteration {iteration}: client-streaming result expected 10, actual {result}");

                await AwaitPhaseAsync(
                    Task.WhenAll(a.Completed, b.Completed, c.Completed),
                    iteration,
                    "server interceptor unwind");
                if (!producer.Started.IsCompleted || !log.SequenceEqual(
                        new[] { "A:before", "B:before", "C:before", "C:after", "B:after", "A:after" }))
                    throw new Exception($"iteration {iteration}: producer or captured interceptor order changed");
            }
            finally
            {
                PreAdmissionStreamDispatcher.BufferedItemObserverForTests = null;
                b.Release();
                await AwaitPhaseAsync(harness.DisposeAsync().AsTask(), iteration, "harness disposal");
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task CompressedClientStreamingShouldWaitForInterceptorAcceptance()
    {
        var buffered = CreateBufferedObservation();
        PreAdmissionStreamDispatcher.BufferedItemObserverForTests =
            (requestId, streamId, compressed) =>
                buffered.TrySetResult(new BufferedObservation(requestId, streamId, compressed));

        var harness = await Harness.CreateAsync(enableCompression: true);
        var gate = new GatedNextServerInterceptor("compressed", new ConcurrentQueue<string>());
        harness.Server.ReplaceInterceptors([gate]);
        var producer = new ProducerProbe();
        try
        {
            var payload = Enumerable.Repeat((byte)0x41, 4096).ToArray();
            var callProxy = harness.Client.CreateProxy<ICompressionService>();
            await callProxy.PrepareAsync(TimeSpan.FromSeconds(10));
            var call = callProxy.Service
                .UploadBytesAsync(SinglePayload(payload, producer)).AsTask();

            await gate.Entered.WaitAsync(PhaseTimeout);
            if (producer.Started.IsCompleted || buffered.Task.IsCompleted ||
                harness.ClientCompression.CompressCount != 0 || harness.ServerCompression.DecompressCount != 0)
                throw new Exception("compressed input ran before interceptor acceptance");
            if (call.IsCompleted)
                throw new Exception("compressed client-stream invocation completed before gated next");

            gate.Release();
            var result = await call.WaitAsync(PhaseTimeout);
            if (result != payload.Length)
                throw new Exception($"compressed client-stream result expected {payload.Length}, actual {result}");
            if (!producer.Started.IsCompleted || harness.ClientCompression.CompressCount == 0 ||
                harness.ServerCompression.DecompressCount == 0)
                throw new Exception("accepted compressed input must use the negotiated compression providers");
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
    public async Task QueuedClientStreamShouldWaitForAdmissionAndInterceptorBeforeProducing()
    {
        InterceptorTestService.ResetDelayedCall();
        var producer = new ProducerProbe();
        var releaseMore = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ClientStreamingGateServerInterceptor();
        var harness = await Harness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 2048;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
            }));
        harness.Server.ReplaceInterceptors([gate]);
        try
        {
            var permitOwner = harness.Service.DelayedAsync().AsTask();
            await InterceptorTestService.DelayedCallStarted.WaitAsync(PhaseTimeout);

            const int activeItemCount = 16;
            const int activeItemSize = 512;
            const int queuedItemSize = 64;
            var callProxy = harness.Client.CreateProxy<ICompressionService>();
            await callProxy.PrepareAsync(TimeSpan.FromSeconds(10));
            var call = callProxy.Service
                .UploadBytesAsync(GatedPayloads(
                    queuedItemSize,
                    activeItemCount,
                    activeItemSize,
                    releaseMore.Task, producer)).AsTask();

            var kernel = ((SharpLinkServer)harness.Server).AdmissionStateKernelForTests!;
            await WaitUntilAsync(() => kernel.QueuedCalls == 1);
            if (producer.Started.IsCompleted || kernel.QueuedBytes <= 0 || kernel.QueuedBytes > 2048)
                throw new Exception("queued input must retain only its bounded request head, with zero producer work");

            InterceptorTestService.ReleaseDelayedCall();
            if (await permitOwner.WaitAsync(PhaseTimeout) != 42)
                throw new Exception("admission permit owner returned an unexpected result");

            await gate.Entered.WaitAsync(PhaseTimeout);
            if (producer.Started.IsCompleted || call.IsCompleted || kernel.QueuedCalls != 0 || kernel.QueuedBytes != 0)
                throw new Exception("admission must release queue accounting without starting input before interceptor acceptance");

            gate.Release();
            await producer.Started.WaitAsync(PhaseTimeout);
            releaseMore.TrySetResult();
            // These accepted items exceed MaxQueuedBytes, but no longer belong to queued retention.
            var expected = queuedItemSize + activeItemCount * activeItemSize;
            var result = await call.WaitAsync(PhaseTimeout);
            if (result != expected)
                throw new Exception($"promoted client-stream result expected {expected}, actual {result}");
        }
        finally
        {
            releaseMore.TrySetResult();
            gate.Release();
            InterceptorTestService.ReleaseDelayedCall();
            await harness.DisposeAsync().AsTask().WaitAsync(PhaseTimeout);
        }
    }

    private static TaskCompletionSource<BufferedObservation> CreateBufferedObservation()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static async IAsyncEnumerable<int> SingleValue(ProducerProbe producer)
    {
        producer.Start();
        await Task.CompletedTask.ConfigureAwait(false);
        yield return 10;
    }

    private static async IAsyncEnumerable<byte[]> SinglePayload(byte[] payload, ProducerProbe producer)
    {
        producer.Start();
        await Task.CompletedTask.ConfigureAwait(false);
        yield return payload;
    }

    private static async IAsyncEnumerable<byte[]> GatedPayloads(
        int firstSize,
        int remainingCount,
        int remainingSize,
        Task releaseRemaining, ProducerProbe producer)
    {
        producer.Start();
        yield return Enumerable.Repeat((byte)0x31, firstSize).ToArray();
        await releaseRemaining.ConfigureAwait(false);
        var payload = Enumerable.Repeat((byte)0x32, remainingSize).ToArray();
        for (var index = 0; index < remainingCount; index++)
        {
            yield return payload;
            await Task.Yield();
        }
    }

    private readonly record struct BufferedObservation(
        long RequestId,
        ushort StreamId,
        bool Compressed);

    private sealed class ProducerProbe
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Started => _started.Task;
        internal void Start() => _started.TrySetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(PhaseTimeout);
        while (!condition())
            await Task.Delay(1, timeout.Token);
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

    private sealed class CountingCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private int _compressCount;
        private int _decompressCount;
        public string WireProfile => _inner.WireProfile;
        internal int CompressCount => Volatile.Read(ref _compressCount);
        internal int DecompressCount => Volatile.Read(ref _decompressCount);
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            return _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        }
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        internal CountingCompressionProvider ClientCompression { get; }
        internal CountingCompressionProvider ServerCompression { get; }
        private readonly RpcProxy<IInterceptorTestService> _proxy;
        public IInterceptorTestService Service => _proxy.Service;

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client, CountingCompressionProvider clientCompression,
            CountingCompressionProvider serverCompression)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
            ClientCompression = clientCompression;
            ServerCompression = serverCompression;
            _proxy = client.CreateProxy<IInterceptorTestService>();
        }

        public static async Task<Harness> CreateAsync(
            bool enableCompression = false,
            Action<SharpLinkServerBuilder>? serverConfigure = null)
        {
            var cts = new CancellationTokenSource();
            var clientCompression = new CountingCompressionProvider();
            var serverCompression = new CountingCompressionProvider();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (enableCompression)
                serverBuilder.UseRuntime(options => options.Compression.Providers.Add(serverCompression))
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
            if (enableCompression)
                clientBuilder.UseRuntime(options => options.Compression.Providers.Add(clientCompression))
                    .UseRequestCompressionPolicy(CreateCompressionPolicy());

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            var harness = new Harness(cts, serverTask, server, client, clientCompression, serverCompression);
            try
            {
                await harness._proxy.PrepareAsync(TimeSpan.FromSeconds(10), cts.Token);
                return harness;
            }
            catch (Exception failure)
            {
                try { await harness.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                throw;
            }
        }

        private static SharpLinkCompressionSendPolicy CreateCompressionPolicy()
            => new()
            {
                MinimumPayloadBytes = 1,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0
            };

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
