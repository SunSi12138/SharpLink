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
            try
            {
                var x = new AwaitingServerInterceptor("X", log);
                var y = new AwaitingServerInterceptor("Y", log);
                var z = new AwaitingServerInterceptor("Z", log);
                harness.Server.ReplaceInterceptors([a, b, c]);

                var call = InvokeClientStreamingAsync(harness.Service);
                await AwaitPhaseAsync(b.Entered, iteration, "server interceptor entry");
                if (call.IsCompleted)
                    throw new Exception($"iteration {iteration}: invocation completed before gated next");

                var observation = await AwaitPhaseAsync(
                    buffered.Task, iteration, "deferred stream buffer entry");
                if (observation.Compressed)
                    throw new Exception($"iteration {iteration}: uncompressed repro buffered a compressed frame");
                if (observation.StreamId == 0)
                    throw new Exception($"iteration {iteration}: client stream used reserved stream ID 0");

                // This proves StreamData reached the deferred route before the generated typed
                // dispatcher can exist. Releasing the interceptor now exercises replay directly.
                harness.Server.ReplaceInterceptors([x, y, z]);
                b.Release();

                var result = await AwaitPhaseAsync(call, iteration, "client-streaming result");
                if (result != 10)
                    throw new Exception($"iteration {iteration}: client-streaming result expected 10, actual {result}");

                await AwaitPhaseAsync(
                    Task.WhenAll(a.Completed, b.Completed, c.Completed),
                    iteration,
                    "server interceptor unwind");
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
    public async Task CompressedClientStreamingFrameShouldReplayAfterInterceptorGate()
    {
        var buffered = CreateBufferedObservation();
        PreAdmissionStreamDispatcher.BufferedItemObserverForTests =
            (requestId, streamId, compressed) =>
                buffered.TrySetResult(new BufferedObservation(requestId, streamId, compressed));

        var harness = await Harness.CreateAsync(enableCompression: true);
        var gate = new GatedNextServerInterceptor("compressed", new ConcurrentQueue<string>());
        harness.Server.ReplaceInterceptors([gate]);
        try
        {
            var payload = Enumerable.Repeat((byte)0x41, 4096).ToArray();
            var call = harness.Client.Get<ICompressionService>()
                .UploadBytesAsync(SinglePayload(payload)).AsTask();

            await gate.Entered.WaitAsync(PhaseTimeout);
            var observation = await buffered.Task.WaitAsync(PhaseTimeout);
            if (observation.StreamId == 0)
                throw new Exception("compressed client stream used reserved stream ID 0");
            if (call.IsCompleted)
                throw new Exception("compressed client-stream invocation completed before gated next");

            // Active compressed StreamData may be decoded by the read loop before it reaches the
            // deferred route so the call owner can re-arbitrate immediately after decompression.
            // The observable contract here is that the interceptor gate still defers delivery and
            // the buffered item replays successfully after the generated typed dispatcher exists.
            gate.Release();
            var result = await call.WaitAsync(PhaseTimeout);
            if (result != payload.Length)
                throw new Exception($"compressed client-stream result expected {payload.Length}, actual {result}");
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
    public async Task QueuedClientStreamShouldPromoteRetentionAfterAdmission()
    {
        InterceptorTestService.ResetDelayedCall();
        var firstBuffered = CreateBufferedObservation();
        var activeBuffered = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bufferedCount = 0;
        PreAdmissionStreamDispatcher.BufferedItemObserverForTests =
            (requestId, streamId, compressed) =>
            {
                firstBuffered.TrySetResult(new BufferedObservation(requestId, streamId, compressed));
                var count = Interlocked.Increment(ref bufferedCount);
                if (count >= 8)
                    activeBuffered.TrySetResult(count);
            };

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
            var call = harness.Client.Get<ICompressionService>()
                .UploadBytesAsync(GatedPayloads(
                    queuedItemSize,
                    activeItemCount,
                    activeItemSize,
                    releaseMore.Task)).AsTask();

            var queuedObservation = await firstBuffered.Task.WaitAsync(PhaseTimeout);
            if (queuedObservation.StreamId == 0)
                throw new Exception("queued client stream used reserved stream ID 0");

            InterceptorTestService.ReleaseDelayedCall();
            if (await permitOwner.WaitAsync(PhaseTimeout) != 42)
                throw new Exception("admission permit owner returned an unexpected result");

            await gate.Entered.WaitAsync(PhaseTimeout);
            releaseMore.TrySetResult();

            // These active-call items exceed the old admission MaxQueuedBytes budget. Promotion
            // must settle the queue accounting before the interceptor releases to the typed stub.
            await activeBuffered.Task.WaitAsync(PhaseTimeout);
            if (call.IsCompleted)
                throw new Exception("promoted client-streaming call completed before gated next");

            gate.Release();
            var expected = queuedItemSize + activeItemCount * activeItemSize;
            var result = await call.WaitAsync(PhaseTimeout);
            if (result != expected)
                throw new Exception($"promoted client-stream result expected {expected}, actual {result}");
        }
        finally
        {
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests = null;
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

    private static async Task<int> InvokeClientStreamingAsync(IInterceptorTestService service)
        => await service.SumStreamAsync(SingleValue(), CancellationToken.None);

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

    private static async IAsyncEnumerable<byte[]> GatedPayloads(
        int firstSize,
        int remainingCount,
        int remainingSize,
        Task releaseRemaining)
    {
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
        public IInterceptorTestService Service { get; }

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
            Service = client.Get<IInterceptorTestService>();
        }

        public static async Task<Harness> CreateAsync(
            bool enableCompression = false,
            Action<SharpLinkServerBuilder>? serverConfigure = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (enableCompression)
                serverBuilder.UseRuntime(ConfigureCompression);
            serverConfigure?.Invoke(serverBuilder);

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(
                () => server.RunAsync(cts.Token).AsTask(),
                CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (enableCompression)
                clientBuilder.UseRuntime(ConfigureCompression);

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        {
            options.Compression.MinimumPayloadBytes = 1;
            options.Compression.MinimumSavingsBytes = 1;
            options.Compression.MinimumSavingsRatio = 0;
            options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
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
