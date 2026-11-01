using Microsoft.Extensions.Logging;

namespace SharpLink.IntegrationTests;

public class OneWayEarlyRejectionDrainIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ImmediatelyRejectedOneWayClientStreamShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        }, dynamicRegistration: true);
        List<Exception> cleanupFailures = [];
        Task? send = null;
        Task<int>? probeTask = null;
        try
        {
            var serviceProxy = harness.Client.CreateProxy<IOneWayInboundDrainService>();
            await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(5));
            var service = serviceProxy.Service;

            var originalConnection = harness.SingleConnection;
            harness.BeginDrain();
            {
                send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
                await send.WaitAsync(PhaseTimeout);

                Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                    "an immediately rejected OneWay request must not invoke the service method");
            }

            await harness.ReleaseAndReregisterAsync();
            await serviceProxy.PrepareAsync(PhaseTimeout);
            probeTask = service.PingAsync(51).AsTask();
            var probe = await probeTask.WaitAsync(PhaseTimeout);
            Ensure(ReferenceEquals(originalConnection, harness.SingleConnection),
                "the same physical server connection must survive rejection, drain and re-registration");
            Ensure(probe == 52,
                "a unary request on the same connection should succeed after the rejected client stream drains to peer terminal");
        }
        catch (Exception primary) { cleanupFailures.Add(primary); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
        await JoinRetainedOperationAsync(cleanupFailures, send);
        await JoinRetainedOperationAsync(cleanupFailures, probeTask);
        ThrowFailures(cleanupFailures);
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodWithExplicitCountShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        List<Exception> failures = [];
        Task? send = null;
        Task<int>? probe = null;
        try
        {
            var service = await harness.CreateUnknownOneWayMethodServiceAsync();
            var originalConnection = harness.SingleConnection;
            send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted && OneWayInboundDrainService.EnumeratedItemCount == 0,
                "an absent method with explicit wire count drains without business invocation or enumeration");
            probe = service.PingAsync(61).AsTask();
            Ensure(await probe.WaitAsync(PhaseTimeout) == 62,
                "the same connection must remain usable after the absent-method stream reaches peer terminal");
            Ensure(ReferenceEquals(originalConnection, harness.SingleConnection) && originalConnection.Session.CanAcceptCalls,
                "explicit-count rejection must not be hidden by reconnecting another physical Session");
            await harness.WaitForRejectedResourcesAsync(originalConnection);
        }
        catch (Exception primary) { failures.Add(primary); }
        try { OneWayInboundDrainService.Release(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        await JoinRetainedOperationAsync(failures, send);
        await JoinRetainedOperationAsync(failures, probe);
        ThrowFailures(failures);
    }

    [Test]
    [NotInParallel]
    public async Task UnknownOneWayMethodWithExplicitCountShouldDrainWithoutCompressedDecode()
    {
        OneWayInboundDrainService.Reset();
        var compression = new CorruptingCompressionProvider();
        var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
            options.Compression.Providers.Add(compression);
        });
        List<Exception> failures = [];
        Task? send = null;
        Task<int>? probe = null;
        try
        {
            var service = await harness.CreateUnknownOneWayMethodServiceAsync();
            var originalConnection = harness.SingleConnection;
            send = service.IgnoreCorruptiblePayloadAndStreamAsync(
                Enumerable.Repeat((byte)0x67, 4096).ToArray(), ManyPayloads(128, 32)).AsTask();
            await send.WaitAsync(PhaseTimeout);
            Ensure(Volatile.Read(ref compression.CompressCount) > 0,
                "the large absent-method request must actually use negotiated compression");
            Ensure(Volatile.Read(ref compression.DecompressCount) == 0,
                "absent-method rejection must precede request decompression");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted && OneWayInboundDrainService.EnumeratedItemCount == 0,
                "absent-method rejection must not invoke or enumerate the business service");
            probe = service.PingAsync(71).AsTask();
            Ensure(await probe.WaitAsync(PhaseTimeout) == 72,
                "explicit-count compressed rejection must drain without a stream-window stall");
            Ensure(ReferenceEquals(originalConnection, harness.SingleConnection) && originalConnection.Session.CanAcceptCalls,
                "the exact physical Session must survive compressed absent-method rejection");
            await harness.WaitForRejectedResourcesAsync(originalConnection);
            Ensure(Volatile.Read(ref compression.DecompressCount) == 0,
                "terminal drain and the small unary probe must not decode the rejected payload");
        }
        catch (Exception primary) { failures.Add(primary); }
        try { OneWayInboundDrainService.Release(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        await JoinRetainedOperationAsync(failures, send);
        await JoinRetainedOperationAsync(failures, probe);
        ThrowFailures(failures);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task BoundOneWayStreamCountMismatchShouldCloseExactSessionBeforeDecode(bool compressed)
    {
        OneWayInboundDrainService.Reset();
        var compression = new CorruptingCompressionProvider();
        var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
            if (compressed) options.Compression.Providers.Add(compression);
        }, observeProtocolViolation: true);
        List<Exception> failures = [];
        Task? publication = null;
        Task? close = null;
        ServerConnectionState? connection = null;
        try
        {
            var bound = await harness.PrepareCountMismatchAsync();
            connection = bound.Connection;
            var disconnected = new TaskCompletionSource<(Exception? Error, bool ProtocolObserved)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Session.OnDisconnected += error => disconnected.TrySetResult(
                (error, harness.ProtocolViolation.IsCompletedSuccessfully));
            var published = harness.PublishCountMismatch(connection, bound.Client, bound.Token, bound.MethodId, bound.Count);
            publication = published.Publication;
            await publication.WaitAsync(PhaseTimeout);
            var violation = await harness.ProtocolViolation.WaitAsync(PhaseTimeout);
            Ensure(violation.RequestId == published.RequestId && violation.Reason == "malformed_frame" &&
                   violation.OriginalFormat == "Client connection was closed because of a protocol violation ({Reason}).",
                "the actual server must classify this exact request as MalformedFrame, not an unrelated transport/heartbeat close");
            // Observe peer-induced terminal BEFORE calling CloseAsync: test cleanup must
            // never manufacture the terminal event that the malformed-wire oracle requires.
            var terminal = await disconnected.Task.WaitAsync(PhaseTimeout);
            // Current dispatch logs the structured cause, then CloseAsync emits the generic
            // Session stopping terminal. Do not pretend OnDisconnected preserves that cause.
            Ensure(terminal.ProtocolObserved && terminal.Error is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "the exact Session must report its actual stopping terminal after the classified protocol violation");
            close = connection.CloseAsync().AsTask();
            await close.WaitAsync(PhaseTimeout);
            await harness.WaitForRejectedResourcesAsync(connection);
            Ensure(connection.LifecycleState == ServerConnectionLifecycleState.Closed &&
                   !connection.Session.IsConnected && connection.Bindings.IsClosed,
                "mismatched declared count must physically close this exact prepared Session and its bindings");
            Ensure(!OneWayInboundDrainService.Entered.IsCompleted && OneWayInboundDrainService.EnumeratedItemCount == 0,
                "malformed count must not enter the generated business method");
            Ensure((Volatile.Read(ref compression.CompressCount) > 0) == compressed,
                "the compressed malformed-count row must actually publish a compressed request");
            Ensure(Volatile.Read(ref compression.DecompressCount) == 0,
                "bound shape validation must terminate before any request decompression");
        }
        catch (Exception primary) { failures.Add(primary); }
        try { OneWayInboundDrainService.Release(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        try { await harness.DisposeAsync(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        await JoinRetainedOperationAsync(failures, publication);
        try { if (connection is not null) close ??= connection.CloseAsync().AsTask(); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        await JoinRetainedOperationAsync(failures, close);
        ThrowFailures(failures);
    }

    private static async Task JoinRetainedOperationAsync(List<Exception> failures, Task? operation)
    {
        if (operation is null) return;
        try { await operation.WaitAsync(PhaseTimeout); }
        catch (Exception error)
        {
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

    // One isolated server, one expected protocol event: no formatted-text matching or
    // process-global listener. The actual RequestId scope distinguishes heartbeat closes.
    private sealed class ProtocolViolationProbe : ILoggerFactory, ILogger
    {
        private readonly AsyncLocal<long?> _requestId = new();
        private readonly TaskCompletionSource<(long? RequestId, string? Reason, string? OriginalFormat)> _violation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<(long? RequestId, string? Reason, string? OriginalFormat)> Violation => _violation.Task;
        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(SharpLinkServer).FullName
                ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("This fixture owns its single observer.");
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _requestId.Value;
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var item in values)
                    if (item.Key == "RequestId" && item.Value is long requestId) _requestId.Value = requestId;
            return new Scope(this, previous);
        }
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Warning || eventId.Id != LogEvents.Connection.ProtocolViolation) return;
            string? reason = null;
            string? originalFormat = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var item in values)
                {
                    if (item.Key == "Reason") reason = item.Value as string;
                    if (item.Key == "{OriginalFormat}") originalFormat = item.Value as string;
                }
            _violation.TrySetResult((_requestId.Value, reason, originalFormat));
        }
        public void Dispose() => _requestId.Value = null;
        private sealed class Scope(ProtocolViolationProbe owner, long? previous) : IDisposable
        {
            public void Dispose() => owner._requestId.Value = previous;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ProtocolViolationProbe? _protocolLog;

        internal Task<(long? RequestId, string? Reason, string? OriginalFormat)> ProtocolViolation
            => (_protocolLog ?? throw new InvalidOperationException("No protocol observer was requested.")).Violation;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client,
            ProtocolViolationProbe? protocolLog)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
            _protocolLog = protocolLog;
        }

        public static async Task<Harness> CreateAsync(
            Action<SharpLinkRuntimeOptions> runtimeConfigure,
            bool dynamicRegistration = false,
            bool observeProtocolViolation = false)
        {
            var protocolLog = observeProtocolViolation ? new ProtocolViolationProbe() : null;
            var cts = new CancellationTokenSource();
            ISharpLinkServer? server = null;
            ISharpLinkClient? client = null;
            Task? serverTask = null;
            Task? connectTask = null;
            try
            {
                var serverBuilder = SharpLinkServerBuilder.Create()
                    .UseTcp(0, IPAddress.Loopback)
                    .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                    .UseRuntime(runtimeConfigure);
                if (protocolLog is not null) serverBuilder.UseLoggerFactory(protocolLog);
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
                var serverToken = cts.Token;
                serverTask = Task.Run(
                    () => server.RunUntilStoppedAsync(serverToken).AsTask(), CancellationToken.None);
                connectTask = client.ConnectAsync(cts.Token).AsTask();
                await connectTask.WaitAsync(PhaseTimeout);
                return new Harness(cts, serverTask, server, client, protocolLog);
            }
            catch (Exception primary)
            {
                await CleanupAsync(primary,
                    () => client?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => new ValueTask(cts.CancelAsync()),
                    () => server?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => connectTask is null ? ValueTask.CompletedTask : new ValueTask(connectTask),
                    () => serverTask is null ? ValueTask.CompletedTask : new ValueTask(serverTask),
                    () => { protocolLog?.Dispose(); return ValueTask.CompletedTask; },
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

        internal async Task<(ServerConnectionState Connection, ClientConnection Client, ulong Token, long MethodId, ushort Count)>
            PrepareCountMismatchAsync()
        {
            var server = (SharpLinkServer)Server;
            var captured = new TaskCompletionSource<(ServerConnectionState, Protocol3BindResult)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeBindingResultForTesting = (connection, result) =>
            {
                captured.TrySetResult((connection, result));
                return Task.CompletedTask;
            };
            try
            {
                var proxy = Client.CreateProxy<IOneWayInboundDrainService>();
                await proxy.PrepareAsync(PhaseTimeout);
                var (connection, result) = await captured.Task.WaitAsync(PhaseTimeout);
                Ensure(result.Status == Protocol3BindStatus.Success, "malformed-frame fixture owns a real successful Bind result");
                var runtime = (SharpLinkRuntimeContext)Client.RuntimeContext;
                var contract = runtime.GeneratedManifests.SelectMany(manifest => manifest.Contracts)
                    .Single(item => item.ContractType == typeof(IOneWayInboundDrainService));
                var method = contract.Methods.Single(item => item.Name == nameof(IOneWayInboundDrainService.IgnoreCorruptiblePayloadAndStreamAsync));
                var entry = connection.Bindings.Capture(result.Token)
                    ?? throw new InvalidOperationException("The real Bind token is absent from its exact Session.");
                var facts = entry.Registration.Facts!.Resolve(method.MethodId);
                Ensure(facts.IsKnown && facts.Kind == RpcMethodKind.OneWay && facts.ClientStreamCount == 1,
                    "the malformed fixture changes only the explicit count, not the actual bound one-stream facts");
                const ushort mismatchedCount = 2;
                Ensure(connection.Session.BindingAuthority.Limits.MaxClientStreamsPerCall >= mismatchedCount,
                    "count mismatch is syntactically valid within the negotiated domain");
                var client = ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(Client)!).Single();
                return (connection, client, result.Token, method.MethodId, mismatchedCount);
            }
            finally { server.BeforeBindingResultForTesting = null; }
        }

        internal (Task Publication, long RequestId) PublishCountMismatch(
            ServerConnectionState connection, ClientConnection client, ulong token, long method, ushort count)
        {
            var session = client.Session;
            var requestId = client.PendingCalls.AllocateRequestId();
            var writer = session.RentFrameWriter();
            IRpcByteBufferWriter? ownedPacket = writer;
            try
            {
                using (writer.BeginPacketScope(ProtocolV2FrameType.OneWayStreamRequest,
                           ProtocolV2FrameFlags.OneWay, unchecked((ulong)requestId)))
                {
                    var prefix = writer.GetSpan(ProtocolV2Constants.OneWayStreamRequestPrefixBytes);
                    BinaryPrimitives.WriteUInt64LittleEndian(prefix, token);
                    BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], method);
                    BinaryPrimitives.WriteUInt16LittleEndian(prefix[16..], count);
                    writer.Advance(ProtocolV2Constants.OneWayStreamRequestPrefixBytes);
                    writer.Write(Enumerable.Repeat((byte)0x67, 4096).ToArray());
                }
                // Preparation owns the writer on failure; enqueue owns its prepared result.
                ownedPacket = null;
                var prepared = session.PrepareOutboundFrame(writer, CancellationToken.None);
                ownedPacket = prepared;
                // Verify the exact prepared (possibly compressed) bytes are structurally
                // legal. The server rejection must be the bound facts mismatch, not an
                // accidental parser, compression-prefix or negotiated-limit rejection.
                var bytes = new ReadOnlySequence<byte>(prepared.WrittenMemory);
                Ensure(ProtocolV2FrameParser.TryReadFrame(ref bytes, connection.Session.RuntimeContext.Protocol,
                           out var header, out var payload) && bytes.IsEmpty &&
                       header.Type == ProtocolV2FrameType.OneWayStreamRequest && header.RequestId == (ulong)requestId,
                    "the malformed fixture publishes one structurally valid Type23 frame for the exact request");
                var envelope = ServerRequestEnvelopeReader.Read(connection.Session, payload, header.Flags,
                    connection.Session.RuntimeContext.Protocol.MaxMetadataBytes,
                    connection.Session.RuntimeContext.TimeProvider, frameType: header.Type);
                Ensure(envelope.BindingId == token && envelope.MethodHash == method && envelope.ClientStreamCount == count,
                    "wire parsing preserves the genuine binding/method and deliberate explicit count mismatch");
                ownedPacket = null;
                if (!session.TryEnqueuePreparedFrame(prepared, false, CancellationToken.None, null,
                        out var emission, out var rejection))
                    throw rejection ?? new InvalidOperationException("Malformed fixture frame was not admitted locally.");
                return (emission.AsTask(), requestId);
            }
            catch (Exception primary)
            {
                if (ownedPacket is not null)
                {
                    try { session.RuntimeContext.Buffers.Return(ownedPacket); }
                    catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                }
                throw;
            }
        }

        internal async Task WaitForRejectedResourcesAsync(ServerConnectionState connection)
        {
            var server = (SharpLinkServer)Server;
            using var timeout = new CancellationTokenSource(PhaseTimeout);
            while (connection.ActiveCalls != 0 || connection.CallCancellations.Count != 0 ||
                   connection.Session.StreamManager.ActiveStreamCount != 0 ||
                   server.ActiveCallCountForDiagnostics != 0 || server.PendingCallAdmissionsForDiagnostics != 0 ||
                   server.ActiveDecodeCountForDiagnostics != 0 || server.RetainedCompressedBytesForDiagnostics != 0 ||
                   server.DecodedBytesInFlightForDiagnostics != 0 || server.PreAdmissionStreamBytesForDiagnostics != 0)
                await Task.Delay(10, timeout.Token);
        }

        private Task? _disposeTask;
        public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync()
        {
            await CleanupAsync(null,
                () => Client.DisposeAsync(),
                () => new ValueTask(_serverCts.CancelAsync()),
                () => Server.DisposeAsync(),
                () => new ValueTask(_serverTask),
                () => { _protocolLog?.Dispose(); return ValueTask.CompletedTask; },
                () => { _serverCts.Dispose(); return ValueTask.CompletedTask; });
        }

        // Failure of one cleanup step must not skip cancellation, endpoint release or join;
        // the original setup error and independent cleanup errors remain observable together.
        private static async ValueTask CleanupAsync(Exception? primary, params Func<ValueTask>[] steps)
        {
            List<Exception>? failures = primary is null ? null : [primary];
            foreach (var step in steps)
            {
                Task? operation = null;
                try
                {
                    operation = step().AsTask();
                    await operation.WaitAsync(PhaseTimeout);
                }
                catch (Exception error)
                {
                    // A deadline is an incomplete physical join, not successful cleanup.
                    // Preserve every actual task fault, including multi-fault tasks.
                    if (error is TimeoutException || operation is not { IsFaulted: true })
                        (failures ??= []).Add(error);
                    if (operation is { IsFaulted: true, Exception: { } aggregate })
                        (failures ??= []).AddRange(aggregate.InnerExceptions);
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
