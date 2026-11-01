
namespace SharpLink.IntegrationTests;

public class OneWayEarlyRejectionDrainIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ImmediatelyRejectedOneWayClientStreamShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        }, dynamicRegistration: true);
        var serviceProxy = harness.Client.CreateProxy<IOneWayInboundDrainService>();
        await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(5));
        var service = serviceProxy.Service;

        var originalConnection = harness.SingleConnection;
        harness.BeginDrain();
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);

            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an immediately rejected OneWay request must not invoke the service method");
        }

        await harness.ReleaseAndReregisterAsync();
        await serviceProxy.PrepareAsync(PhaseTimeout);
        var probe = await service.PingAsync(51).AsTask().WaitAsync(PhaseTimeout);
        Ensure(ReferenceEquals(originalConnection, harness.SingleConnection),
            "the same physical server connection must survive rejection, drain and re-registration");
        Ensure(probe == 52,
            "a unary request on the same connection should succeed after the rejected client stream drains to peer terminal");
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodShapeShouldTerminateInsteadOfStallingOversizedStream()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var service = await harness.CreateUnknownOneWayMethodServiceAsync();
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            var failure = await CaptureFailureAsync(send);

            Ensure(failure is not null,
                "an unresolved OneWay method shape should terminate the connection instead of completing successfully");
            Ensure(failure is not TimeoutException,
                "an unresolved OneWay method shape must not leave oversized client-stream data stalled behind receive flow control");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an unresolved OneWay method must not invoke the service method");
        }
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodShapeShouldTerminateBeforeCompressedDecodeAndStreamWindowStalls()
    {
        OneWayInboundDrainService.Reset();
        var compression = new CorruptingCompressionProvider();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
            options.Compression.Providers.Add(compression);
        });
        var service = await harness.CreateUnknownOneWayMethodServiceAsync();
        {
            var requestPayload = Enumerable.Repeat((byte)0x67, 4096).ToArray();
            var send = service.IgnoreCorruptiblePayloadAndStreamAsync(
                requestPayload,
                ManyPayloads(128, 32)).AsTask();
            var failure = await CaptureFailureAsync(send);

            Ensure(Volatile.Read(ref compression.CompressCount) > 0,
                "the large OneWay request payload must use the negotiated compression provider");
            Ensure(Volatile.Read(ref compression.DecompressCount) == 0,
                "an unresolved OneWay method shape must terminate before invoking the request decompressor");
            Ensure(failure is not null,
                "an unresolved compressed OneWay shape should terminate the connection");
            Ensure(failure is not TimeoutException,
                "an unresolved compressed OneWay shape must not leave subsequent StreamData stalled behind receive flow control");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "an unresolved compressed OneWay request must not invoke the service method");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(PhaseTimeout);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async IAsyncEnumerable<byte[]> ManyPayloads(
        int count,
        int size,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Enumerable.Repeat((byte)0x4d, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return payload;
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CorruptingCompressionProvider : ISharpLinkCompressionProvider
    {
        internal int CompressCount;
        internal int DecompressCount;

        public string WireProfile => "corrupt-oneway-request";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxOutputBytes < 1)
                throw new InvalidOperationException("test compression output budget is empty");

            Interlocked.Increment(ref CompressCount);
            var span = output.GetSpan(1);
            span[0] = 0x7f;
            output.Advance(1);
            return true;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _ = input;
            _ = output;
            _ = maxOutputBytes;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DecompressCount);
            throw new InvalidDataException("intentional corrupt compressed request body");
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
            Action<SharpLinkRuntimeOptions> runtimeConfigure,
            bool dynamicRegistration = false)
        {
            var cts = new CancellationTokenSource();
            ISharpLinkServer? server = null;
            ISharpLinkClient? client = null;
            Task? serverTask = null;
            try
            {
                var serverBuilder = SharpLinkServerBuilder.Create()
                    .UseTcp(0, IPAddress.Loopback)
                    .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                    .UseRuntime(runtimeConfigure);
                var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                    .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                    .UseRuntime(runtimeConfigure);
                if (dynamicRegistration)
                {
                    // This real assembly must not already belong to the static registry.
                    var manifests = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
                        .Where(manifest => !ReferenceEquals(manifest.OwnerAssembly, typeof(IOneWayInboundDrainService).Assembly))
                        .ToArray();
                    serverBuilder.UseGeneratedManifestSource(new FixedGeneratedManifestSource(manifests));
                }
                var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
                server = serverBuilder.Build();
                client = clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port).Build();
                if (dynamicRegistration)
                {
                    var assembly = typeof(IOneWayInboundDrainService).Assembly;
                    var serverRegistration = server.RegisterAssembly(assembly);
                    Ensure(serverRegistration.Succeeded, $"real server fixture registration: {serverRegistration.Error}");
                }
                serverTask = Task.Run(
                    () => server.RunUntilStoppedAsync(cts.Token).AsTask(), CancellationToken.None);
                await client.ConnectAsync(cts.Token).AsTask().WaitAsync(PhaseTimeout);
                return new Harness(cts, serverTask, server, client);
            }
            catch (Exception primary)
            {
                await CleanupAsync(primary,
                    () => client?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => new ValueTask(cts.CancelAsync()),
                    () => server?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => ObserveServerAsync(serverTask, cts),
                    () => { cts.Dispose(); return ValueTask.CompletedTask; });
                throw;
            }
        }

        internal ServerConnectionState SingleConnection
            => ServerRegistryTestAccessor.ActiveConnections((SharpLinkServer)Server).Values.Single();

        internal void BeginDrain()
        {
            var module = ServerRegistryTestAccessor.DynamicModule(Server, typeof(IOneWayInboundDrainService).Assembly);
            Ensure(module.TryBeginDraining(), "the actually registered fixture module enters draining once");
        }

        internal async Task ReleaseAndReregisterAsync()
        {
            var assembly = typeof(IOneWayInboundDrainService).Assembly;
            Ensure((await Server.UnregisterAssemblyAsync(assembly, PhaseTimeout).AsTask().WaitAsync(PhaseTimeout)).ReferencesReleased,
                "the drained server fixture must release its old exact registration");
            // The client's static local identity remains live. Its actual cached call site
            // must observe wire retirement before Prepare can claim readiness for the new token.
            var channel = Client.CreateContractChannel(Client.GetLocalContract<IOneWayInboundDrainService>());
            var site = channel as SharpLinkClient.ContractCallSite
                ?? throw new InvalidOperationException("The fixture expects its actual static client contract site.");
            using var retirementBudget = new CancellationTokenSource(PhaseTimeout);
            while (site.HasBoundRoute)
                await Task.Delay(10, retirementBudget.Token);
            var serverRegistration = Server.RegisterAssembly(assembly);
            Ensure(serverRegistration.Succeeded, $"server fixture re-registration: {serverRegistration.Error}");
        }

        internal async Task<IOneWayInboundDrainService> CreateUnknownOneWayMethodServiceAsync()
        {
            var assembly = typeof(IOneWayInboundDrainService).Assembly;
            var runtime = (SharpLinkRuntimeContext)Client.RuntimeContext;
            var manifest = runtime.GeneratedManifests.Single(candidate => ReferenceEquals(candidate.OwnerAssembly, assembly));
            Ensure(manifest is PreparedGeneratedManifest, "negative wire fixture uses the actually prepared generated manifest");
            var descriptor = manifest.Contracts.Single(contract => contract.ContractType == typeof(IOneWayInboundDrainService));
            var absentMethod = long.MinValue;
            while (descriptor.Methods.Any(method => method.MethodId == absentMethod)) absentMethod++;
            var channel = Client.CreateContractChannel(Client.GetLocalContract<IOneWayInboundDrainService>());
            await channel.PrepareAsync(PhaseTimeout);
            var provider = RpcGeneratedCodecResolver.GetProvider(Client.RuntimeContext, assembly);
            return (IOneWayInboundDrainService)descriptor.ProxyFactory(
                new UnknownOneWayMethodChannel(channel, absentMethod), provider);
        }

        private bool _disposed;
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await CleanupAsync(null,
                () => Client.DisposeAsync(),
                () => new ValueTask(_serverCts.CancelAsync()),
                () => Server.DisposeAsync(),
                () => ObserveServerAsync(_serverTask, _serverCts),
                () => { _serverCts.Dispose(); return ValueTask.CompletedTask; });
        }

        private static async ValueTask ObserveServerAsync(Task? task, CancellationTokenSource cancellation)
        {
            if (task is null) return;
            try { await task.WaitAsync(PhaseTimeout); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        // Failure of one cleanup step must not skip cancellation, endpoint release or join;
        // the original setup error and independent cleanup errors remain observable together.
        private static async ValueTask CleanupAsync(Exception? primary, params Func<ValueTask>[] steps)
        {
            List<Exception>? failures = primary is null ? null : [primary];
            foreach (var step in steps)
            {
                try { await step(); }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null) throw new AggregateException("Fixture setup/cleanup failures.", failures);
        }

        // Real identity, codecs, metadata and stream writers are retained. Only an absent
        // wire MethodId is injected; no unknown-facts stub or fabricated binding is installed.
        private sealed class UnknownOneWayMethodChannel(IRpcContractChannel inner, long absentMethod) : IRpcContractChannel
        {
            public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;
            public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
                => inner.PrepareAsync(timeout, cancellationToken);
            public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
                in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
                => inner.InvokeUnaryAsync(method, in request, requestCodec, responseCodec, metadata, cancellationToken);
            public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
                in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
                SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => inner.InvokeOneWayAsync(method with { MethodId = absentMethod }, in request, requestCodec,
                    in streams, metadata, cancellationToken);
            public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
                in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => inner.InvokeClientStreamingAsync(method, in request, requestCodec, responseCodec,
                    in streams, metadata, cancellationToken);
            public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
                in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
                => inner.InvokeServerStreamingAsync(method, in request, requestCodec, responseCodec, metadata, cancellationToken);
            public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
                in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => inner.InvokeDuplexStreamingAsync(method, in request, requestCodec, responseCodec,
                    in streams, metadata, cancellationToken);
        }
    }
}
