using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", true, true, false)]
    [Arguments("tcp", false, false, false)]
    [Arguments("shared-memory", true, false, false)]
    [Arguments("shared-memory", false, true, false)]
    [Arguments("tcp", false, false, true)]
    [Arguments("shared-memory", true, true, true)]
    public async Task EarlyInputReturnsCreditAfterOrdinaryCapacityRejection(
        string transport, bool duplex, bool compressed, bool queued)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = guard.Token;
        using var releaseRejection = new ManualResetEventSlim();
        var inAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstItemQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var builder = SharpLinkServerBuilder.Create()
            .UseAdmissionControl(options =>
            {
                options.UsePartition(_ =>
                {
                    if (!queued && Interlocked.Increment(ref calls) == 2)
                    {
                        inAdmission.TrySetResult();
                        releaseRejection.Wait(ct);
                    }
                    return "normal";
                }, partition =>
                {
                    partition.MaxPartitions = 1;
                    partition.UseConcurrency(1);
                });
                if (queued)
                {
                    options.MaxQueuedCalls = 1;
                    options.MaxQueuedBytes = 4096;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(1);
                }
            });
        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout();
        var sender = new ObservedInputCompression();
        var receiver = new ObservedInputCompression();
        if (compressed)
        {
            builder.UseRuntime(options => options.Compression.Providers.Add(receiver));
            clientBuilder.UseRuntime(options => options.Compression.Providers.Add(sender))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    MinimumPayloadBytes = 256,
                    MinimumSavingsBytes = 1
                });
        }
        if (transport == "tcp")
        {
            builder.UseTcp(0, IPAddress.Loopback);
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port);
        }
        else
        {
            var name = $"sharplink-early-duplex-reject-{Guid.NewGuid():N}";
            builder.UseSharedMemory(name);
            clientBuilder.UseSharedMemory(name);
        }
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = (SharpLinkClient)clientBuilder.Build();
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var unary = client.CreateProxy<ITestService>();
        await unary.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var streaming = client.CreateProxy<ICompressionService>();
        await streaming.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var connection = NStudyClientConnection(client);
        var peer = NStudyServerConnection(server);
        var flow = ReadInputFlow(connection.Session);
        var peerFlow = ReadInputFlow(peer.Session);
        var initialCredit = flow.SendConnectionCredit;
        var initialDropped = peer.Session.StreamManager.DroppedStreamFrames;
        TestService.ResetBlockingAdd();
        var active = unary.Service.BlockingAddAsync(1, 2, ct).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(ct);
            var rejected = duplex
                ? ReadAll(streaming.Service.DuplexBytesAsync(OneInput(ct)))
                : streaming.Service.UploadBytesAsync(OneInput(ct)).AsTask();
            if (!queued)
                await inAdmission.Task.WaitAsync(ct);
            await firstItemQueued.Task.WaitAsync(ct);
            var outstanding = initialCredit - flow.SendConnectionCredit;
            Ensure(outstanding > 0 && (!compressed || sender.Encoded == 1),
                "first input is published in the intended wire form and consumes credit");
            if (queued)
                await WaitForNStudyAsync(() => server.PreAdmissionStreamBytesForDiagnostics > 0 &&
                    peerFlow.RetainedReceiveStreamCount == 1, ct);
            releaseRejection.Set();
            try
            {
                await rejected.WaitAsync(ct);
                throw new InvalidOperationException("A full admission gate must reject the stream.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
            }
            await WaitForNStudyAsync(() => connection.PendingCalls.NStudyProducerCount == 0 &&
                flow.SendConnectionCredit == initialCredit && flow.RetainedSendStreamCount == 0 &&
                peerFlow.RetainedReceiveStreamCount == 0 &&
                (queued || peer.Session.StreamManager.DroppedStreamFrames > initialDropped), ct);
            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(ct) == 3 &&
                await unary.Service.AddAsync(20, 22) == 42 &&
                ReferenceEquals(NStudyClientConnection(client), connection),
                "the same session remains usable after rejection");
            Ensure(flow.SendConnectionCredit == initialCredit && flow.RetainedSendStreamCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 && receiver.Decoded == 0,
                $"rejection must refund the input: {initialCredit - flow.SendConnectionCredit} bytes, " +
                $"{flow.RetainedSendStreamCount} retained send entries, " +
                $"{peer.Session.StreamManager.DroppedStreamFrames - initialDropped} dropped frames");
        }
        finally
        {
            releaseRejection.Set();
            TestService.ReleaseBlockingAdd();
        }

        async IAsyncEnumerable<byte[]> OneInput([EnumeratorCancellation] CancellationToken producerToken)
        {
            yield return new byte[compressed ? 4096 : 256];
            firstItemQueued.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, producerToken);
        }

        async Task ReadAll(IAsyncEnumerable<byte[]> output)
        {
            await foreach (var _ in output.WithCancellation(ct)) { }
        }
    }

    private static StreamFlowController ReadInputFlow(RpcSession session)
        => ((RpcSessionProtocolState)typeof(RpcSession).GetField("_protocolState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).FlowController!;

    private sealed class ObservedInputCompression : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private int _encoded;
        private int _decoded;
        public string WireProfile => _inner.WireProfile;
        internal int Encoded => Volatile.Read(ref _encoded);
        internal int Decoded => Volatile.Read(ref _decoded);

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            if (!_inner.TryCompress(input, output, maxOutputBytes, cancellationToken))
                return false;
            Interlocked.Increment(ref _encoded);
            return true;
        }

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decoded);
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp", "client-stream")]
    [Arguments("tcp", "client-stream-eof")]
    [Arguments("tcp", "duplex")]
    [Arguments("tcp", "duplex-eof")]
    [Arguments("shared-memory", "client-stream")]
    [Arguments("shared-memory", "client-stream-eof")]
    [Arguments("shared-memory", "duplex")]
    [Arguments("shared-memory", "duplex-eof")]
    public async Task NStudyPreparedTwoWayInputUsesAcceptedRouteAndOriginalOwners(string transport, string shape)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var serverLoader = new PluginLoadContext($"prepared-input-server-{transport}-{shape}", directory);
        var newLoader = new PluginLoadContext($"prepared-input-r2-{transport}-{shape}", directory);
        try
        {
            var serverAssembly = serverLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            var service = FeedType(serverAssembly, "DeliveryUpdates");
            var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
            var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(serverAssembly, out var dynamicManifest);
            Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "generated delivery manifests load");
            var aCid = dynamicManifest!.Contracts.Single(c => c.ContractType == FeedType(serverAssembly, "IDeliveryUpdates")).ContractId;
            var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([staticManifest]);
            var observer = new NStudyUnarySiteObserver(aCid);
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider).AddInterceptor(observer).UseAdmissionControl(options =>
            {
                options.AddContract(aCid, rule => rule.UseConcurrency(1));
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(15);
            });
            var memory = transport == "shared-memory" ? $"sharplink-n-prepared-input-{Guid.NewGuid():N}" : null;
            if (memory is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memory);
            var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memory);
            Ensure(server.RegisterAssembly(serverAssembly).Succeeded, "real server registers its typed service");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var session = NStudyClientSession(client);
            var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var (retired, a2) = await ExerciseNStudyPreparedTwoWayAsync(server, client, service, directory, path, newAssembly,
                observer, trace, aCid, bCid, b, bBinding, memory, shape, ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (var i = 0; retired.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retired.IsAlive && ReferenceEquals(NStudyClientSession(client), session) &&
                   session.CaptureNStudyBinding(aCid) == a2.BindingId && session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await b.TouchAsync(4, ct) == 104,
                "the old client input context collects without closing the current Session or unrelated route");
            trace.Record("ReplacedInputContextCollectsOnSameSession", a2, bBinding);
        }
        finally
        {
            newLoader.Unload();
            serverLoader.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Old, NStudyAck Ready)> ExerciseNStudyPreparedTwoWayAsync(
        SharpLinkServer server, SharpLinkClient client, Type service, string directory, string path, Assembly newAssembly,
        NStudyUnarySiteObserver observer, NStudyTrace trace, long aCid, long bCid, IShutdownCleanupProbe b, NStudyAck bBinding,
        string? memory, string shape, CancellationToken ct)
    {
        var loader = new PluginLoadContext("prepared-input-client-r1", directory);
        var retired = new WeakReference(loader);
        var oldAssembly = loader.LoadFromAssemblyPath(path);
        var queuedInput = new NStudyInput();
        var activeInput = new NStudyInput();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var duplex = shape.StartsWith("duplex", StringComparison.Ordinal);
            var earlyEof = shape.EndsWith("-eof", StringComparison.Ordinal);
            Ensure(client.RegisterAssembly(oldAssembly).Succeeded, "real dynamic client generation registers");
            var oldModule = GetDynamicModule(client, oldAssembly);
            var serverModule = GetDynamicModule(server, service.Assembly);
            var oldCodec = FeedType(oldAssembly, "FeedReadingCodec");
            var newCodec = FeedType(newAssembly, "FeedReadingCodec");
            var serverCodec = FeedType(service.Assembly, "FeedReadingCodec");
            var original = BuildNStudyTwoWay(client, oldAssembly, duplex, "generated-input-r1");
            var a1 = await original.Prepare(TimeSpan.FromSeconds(5), ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var bindSequence = session.NStudyBindOperationForDiagnostics;
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            Ensure(a1.Authority == bBinding.Authority && a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "prepared input uses the actual authenticated data Session");

            var occupying = original.Occupy(ct).AsTask();
            await ReadNStudyTask(service, "WaitingForApproval").WaitAsync(ct);
            var queued = original.Generated(queuedInput, ct);
            await WaitForNStudyAsync(() => kernel.QueuedCalls == 1 && connection.PendingCalls.NStudyProducerCount == 1, ct);
            await WaitForNStudyAsync(() => server.PreAdmissionStreamBytesForDiagnostics > 0, ct);
            Ensure(queuedInput.Enumerators == 1 && FeedCount(oldCodec, "Serialized") == 1,
                "the input is sent while the ordinary admission queue is occupied");
            Ensure(FeedCount(serverCodec, "Deserialized") == 0 && !queued.IsCompleted &&
                   serverModule.RemainingStreams == 0 && await b.TouchAsync(1, ct) == 101,
                "queued input is not decoded or dispatched while admission is held");
            trace.Record("PreparedTwoWayPreservesQueuedServerAdmission", a1, bBinding);

            if (earlyEof)
            {
                queuedInput.ReleaseItems();
                queuedInput.ReleaseDisposal();
                await WaitForNStudyAsync(() => connection.PendingCalls.NStudyProducerCount == 0, ct);
                Ensure(await b.TouchAsync(5, ct) == 105 &&
                    FeedCount(oldCodec, "Serialized") == 2 &&
                    FeedCount(serverCodec, "Deserialized") == 0 &&
                    server.PreAdmissionStreamBytesForDiagnostics > 0,
                    "the queued request retains its raw input after the producer sends EOF");
            }

            service.GetMethod("Approve")!.Invoke(null, null);
            Ensure(await occupying.WaitAsync(ct) == 42, "the ordinary approval releases one server permit");
            await queuedInput.Started.WaitAsync(ct);
            if (!earlyEof)
            {
                await WaitForNStudyAsync(() => FeedCount(service, "Reconciled") == 3, ct);
                Ensure(queuedInput.Enumerators == 1 && oldModule.RemainingStreams == 2 &&
                       serverModule.RemainingStreams == 1 && FeedCount(oldCodec, "Serialized") == 1,
                    "the single accepted generated producer uses the exact typed Codec and independent local ownership");
            }
            trace.Record("PreparedTwoWayUsesExactTypedProducer", a1);
            queuedInput.ReleaseItems();
            queuedInput.ReleaseDisposal();
            Ensure((await queued.WaitAsync(ct)).SequenceEqual(duplex ? [6, 14] : [10]),
                "normal generated typed output contains exactly the accepted inputs");
            await WaitForNStudyAsync(() => server.PreAdmissionStreamBytesForDiagnostics == 0 &&
                kernel.ActivePermits == 0 && serverModule.RemainingStreams == 0 &&
                oldModule.RemainingStreams == 0 && connection.ActiveCallCount == 0, ct);
            server.DisableAdmissionControl();

            var running = original.Manual(activeInput, Metadata("manual-input-r1"), cancel.Token);
            await activeInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => FeedCount(service, "Reconciled") == 13 &&
                (!duplex || FeedCount(oldCodec, "Deserialized") == 3), ct);
            var requestId = peer.LastAcceptedRequestId;
            var producerToken = activeInput.ProducerToken;
            var handle = producerToken.WaitHandle.SafeWaitHandle;
            Ensure(oldModule.RemainingStreams == 2 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   FeedCount(oldCodec, "Serialized") == 3 && observer.Calls.Last().Label == "manual-input-r1" &&
                   observer.Calls.Any(call => call.Label == "generated-input-r1" &&
                       call.Method.Kind == (duplex ? RpcMethodKind.DuplexStreaming : RpcMethodKind.ClientStreaming)) &&
                   session.NStudyBindOperationForDiagnostics == bindSequence &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId,
                "the manual typed producer and generated caller use their one original prepared site");
            trace.Record("GeneratedAndManualSharePreparedInputSite", a1);

            var replacing = client.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            var current = BuildNStudyTwoWay(client, newAssembly, duplex, "generated-input-r2");
            var a2 = await current.Prepare(TimeSpan.FromSeconds(5), ct);
            Ensure((await current.Generated(Values(11, 13), ct).WaitAsync(ct)).SequenceEqual(duplex ? [22, 26] : [24]) &&
                   oldModule.State == SharpLinkDynamicModuleState.Draining && oldModule.RemainingStreams == 2 &&
                   !replacing.IsCompleted && a2.Authority == a1.Authority && a2.Target == a1.Target && a2.BindingId != a1.BindingId &&
                   FeedCount(newCodec, "Serialized") == 2 && FeedCount(oldCodec, "Disposed") == 0 &&
                   await b.TouchAsync(2, ct) == 102, "new site and unrelated B proceed on the same Session while old input is live");
            trace.Record("NewLocalSiteAndBOkWhileOriginalInputIsActive", a1, a2);

            cancel.Cancel();
            await activeInput.Disposing.WaitAsync(ct);
            await ExpectNStudyCallerCancellation(running, cancel.Token, ct);
            await WaitForNStudyAsync(() => !peer.CallCancellations.TryGetValue(requestId, out _) &&
                FeedCount(service, "TwoWayInputExits") == 3, ct);
            Ensure(connection.PendingCalls.ActiveCount == 0 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   producerToken.IsCancellationRequested && !handle.IsClosed && activeInput.ExitCount == 0 &&
                   oldModule.RemainingStreams >= 1 && !replacing.IsCompleted &&
                   FeedCount(oldCodec, "Disposed") == 0 && await b.TouchAsync(3, ct) == 103,
                "normal logical cancellation and the peer exit do not dispose the original physically active writer or its CTS");
            trace.Record("LocalCancellationRetainsOriginalPhysicalProducer", a1, a2);

            activeInput.ReleaseDisposal();
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "old client input retires after the real producer exits");
            await WaitForNStudyAsync(() => connection.ActiveCallCount == 0 && peer.ActiveCalls == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0) && session.StreamManager.ActiveStreamCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0, ct);
            Ensure(activeInput.Enumerators == 1 && activeInput.ExitCount == 1 && handle.IsClosed &&
                   FeedCount(oldCodec, "Disposed") == 1 && FeedCount(newCodec, "Disposed") == 0 &&
                   FeedCount(serverCodec, "Deserialized") == 5 && FeedCount(service, "Reconciled") == 37 &&
                   session.CaptureNStudyBinding(aCid) == a2.BindingId && session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
                "the original physical producer exits once; CTS, codec, pending and route accounting recover");
            trace.Record("OriginalPhysicalExitReleasesBothRoutesAndCodec", a1, a2);
            if (!earlyEof)
                await VerifyEarlyInputInterceptorRejectionAsync(server, connection, peer, current,
                    serverCodec, b, ct);
            if (!duplex)
                await VerifyEarlyInputUnusedByHandlerAsync(server, connection, peer, current,
                    b, earlyEof, ct);
            return (retired, a2);
        }
        finally
        {
            queuedInput.ReleaseItems();
            queuedInput.ReleaseDisposal();
            activeInput.ReleaseItems();
            activeInput.ReleaseDisposal();
            service.GetMethod("Approve")!.Invoke(null, null);
            loader.Unload();
        }
    }

    private static async Task VerifyEarlyInputInterceptorRejectionAsync(
        SharpLinkServer server, ClientConnection connection, ServerConnectionState peer,
        NStudyPreparedTwoWay current, Type serverCodec, IShutdownCleanupProbe sibling, CancellationToken ct)
    {
        var gate = new GatedInputRejection();
        var source = new NStudyInput();
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = ReadInputFlow(connection.Session);
        var peerFlow = ReadInputFlow(peer.Session);
        var credit = flow.SendConnectionCredit;
        var decodes = FeedCount(serverCodec, "Deserialized");
        server.ReplaceInterceptors([gate]);
        PreAdmissionStreamDispatcher.BufferedItemObserverForTests = (_, _, _) => buffered.TrySetResult();
        try
        {
            var rejected = current.Generated(source, ct);
            await gate.Entered.Task.WaitAsync(ct);
            await buffered.Task.WaitAsync(ct);
            Ensure(flow.SendConnectionCredit < credit && peerFlow.RetainedReceiveStreamCount == 1,
                "the dynamic invocation owns one buffered input while the interceptor is held");
            gate.Release.TrySetResult();
            await source.Disposing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            source.ReleaseDisposal();
            try
            {
                await rejected.WaitAsync(ct);
                throw new InvalidOperationException("The interceptor should reject this input.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.PermissionDenied) { }
            await WaitForNStudyAsync(() => flow.SendConnectionCredit == credit &&
                flow.RetainedSendStreamCount == 0 && peerFlow.RetainedReceiveStreamCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 && connection.ActiveCallCount == 0, ct);
            Ensure(source.ExitCount == 1 && FeedCount(serverCodec, "Deserialized") == decodes,
                "dynamic rejection drains an open producer and its retained credit without business decoding");
        }
        finally
        {
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests = null;
            server.ReplaceInterceptors([]);
            gate.Release.TrySetResult();
            source.ReleaseItems();
            source.ReleaseDisposal();
        }
        Ensure(await sibling.TouchAsync(6, ct) == 106, "the same Session continues after interceptor rejection");
    }

    private static async Task VerifyEarlyInputUnusedByHandlerAsync(
        SharpLinkServer server, ClientConnection connection, ServerConnectionState peer,
        NStudyPreparedTwoWay current, IShutdownCleanupProbe sibling, bool earlyEof, CancellationToken ct)
    {
        var gate = new GatedInputRejection(reject: false);
        var source = new NStudyInput();
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = ReadInputFlow(connection.Session);
        var peerFlow = ReadInputFlow(peer.Session);
        var credit = flow.SendConnectionCredit;
        server.ReplaceInterceptors([gate]);
        PreAdmissionStreamDispatcher.BufferedItemObserverForTests = (_, _, _) => buffered.TrySetResult();
        try
        {
            var result = current.IgnoreInput(source, ct);
            await gate.Entered.Task.WaitAsync(ct);
            await buffered.Task.WaitAsync(ct);
            if (earlyEof)
            {
                source.ReleaseItems();
                source.ReleaseDisposal();
                await WaitForNStudyAsync(() => connection.PendingCalls.NStudyProducerCount == 0, ct);
                Ensure(await sibling.TouchAsync(7, ct) == 107, "the peer observes the published input EOF");
            }
            Ensure(flow.SendConnectionCredit < credit && peerFlow.RetainedReceiveStreamCount == 1,
                "the handler has early input it may choose not to read");
            gate.Release.TrySetResult();
            if (!earlyEof)
            {
                await source.Disposing.WaitAsync(TimeSpan.FromSeconds(5), ct);
                source.ReleaseDisposal();
            }
            Ensure(await result.WaitAsync(ct) == 42, "the real handler returns without enumerating input");
            await WaitForNStudyAsync(() => flow.SendConnectionCredit == credit &&
                flow.RetainedSendStreamCount == 0 && peerFlow.RetainedReceiveStreamCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 && connection.ActiveCallCount == 0, ct);
            Ensure(source.ExitCount == 1 && await sibling.TouchAsync(8, ct) == 108,
                "the original typed input and both credit ledgers finish even when the handler never enumerates");
        }
        finally
        {
            PreAdmissionStreamDispatcher.BufferedItemObserverForTests = null;
            server.ReplaceInterceptors([]);
            gate.Release.TrySetResult();
            source.ReleaseItems();
            source.ReleaseDisposal();
        }
    }

    private sealed class GatedInputRejection(bool reject = true) : ISharpLinkServerInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (reject)
                    throw new SharpLinkException(SharpLinkErrorCode.PermissionDenied, "Input rejected by policy.");
            }
            await next(context).ConfigureAwait(false);
        }
    }

    private static NStudyPreparedTwoWay BuildNStudyTwoWay(SharpLinkClient client, Assembly assembly, bool duplex, string label)
        => (NStudyPreparedTwoWay)typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(BuildNStudyTypedTwoWay),
            BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(FeedType(assembly, "IDeliveryUpdates"), FeedType(assembly, "FeedReading"))
            .Invoke(null, [client, assembly, duplex, label])!;

    private static NStudyPreparedTwoWay BuildNStudyTypedTwoWay<TContract, TReading>(
        SharpLinkClient client, Assembly assembly, bool duplex, string label) where TContract : IService
    {
        var site = CreateNStudyPublicStreaming<TContract>(client, assembly, label);
        var reconcile = site.Method("ReconcileAsync");
        var mirror = site.Method("MirrorAsync");
        var typedCodec = site.Codecs.GetCodec<TReading>();
        var responseCodec = site.Codecs.GetCodec<int>();
        var mirrorProxy = typeof(TContract).GetMethod("MirrorAsync")!;
        async Task<int[]> Generated(IAsyncEnumerable<int> readings, CancellationToken ct)
        {
            var input = AsDeliveryReading<TReading>(readings);
            return duplex
                ? await CollectAsync(ProjectFeed<TReading>(mirrorProxy.Invoke(site.Service, [input, ct])!, ct))
                : [await InvokeValueTaskAsync<int>(site.Service, typeof(TContract), "ReconcileAsync", input, ct)];
        }
        async Task<int[]> Manual(IAsyncEnumerable<int> readings, SharpLinkMetadata metadata, CancellationToken ct)
        {
            var writer = new NStudyManualDeliveryWriter<TReading>(AsDeliveryReading<TReading>(readings), typedCodec);
            return duplex
                ? await CollectAsync(ProjectFeed<TReading>(
                    site.Channel.InvokeDuplexStreamingAsync(mirror, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                        typedCodec, writer, metadata, ct), ct))
                : [await site.Channel.InvokeClientStreamingAsync(reconcile, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                    responseCodec, writer, metadata, ct)];
        }
        return new(site.Prepare, ct => InvokeValueTaskAsync<int>(site.Service, typeof(TContract), "WaitForApprovalAsync", ct),
            Generated, Manual, (readings, ct) => InvokeValueTaskAsync<int>(site.Service,
                typeof(TContract), "ReconcileWithoutReadingAsync", AsDeliveryReading<TReading>(readings), ct).AsTask());
    }

    private sealed record NStudyPreparedTwoWay(
        Func<TimeSpan, CancellationToken, ValueTask<NStudyAck>> Prepare,
        Func<CancellationToken, ValueTask<int>> Occupy,
        Func<IAsyncEnumerable<int>, CancellationToken, Task<int[]>> Generated,
        Func<IAsyncEnumerable<int>, SharpLinkMetadata, CancellationToken, Task<int[]>> Manual,
        Func<IAsyncEnumerable<int>, CancellationToken, Task<int>> IgnoreInput);
}
