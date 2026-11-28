using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyPreparedSiteOneWayKeepsLocalCompletionAndOriginalInputOwner(string transport)
    {
        const int streamWindow = 128;
        const int connectionWindow = 256;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var serverLoader = new PluginLoadContext($"delivery-server-{transport}", directory);
        var newLoader = new PluginLoadContext($"delivery-client-r2-{transport}", directory);
        try
        {
            var serverAssembly = serverLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            var service = FeedType(serverAssembly, "DeliveryUpdates");
            var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
            var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(serverAssembly, out var dynamicManifest);
            Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "the actual static and dynamic generated manifests load");
            var aCid = dynamicManifest!.Contracts.Single(c => c.ContractType == FeedType(serverAssembly, "IDeliveryUpdates")).ContractId;
            var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([staticManifest]);
            var observer = new NStudyUnarySiteObserver(aCid);
            void ConfigureWindows(SharpLinkRuntimeOptions options)
            {
                options.FlowControl.StreamReceiveWindowBytes = streamWindow;
                options.FlowControl.ConnectionReceiveWindowBytes = connectionWindow;
            }
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider).UseRuntime(ConfigureWindows).AddInterceptor(observer)
                .UseAdmissionControl(options =>
                {
                    options.AddContract(aCid, rule => rule.UseConcurrency(1));
                    options.MaxQueuedCalls = 3;
                    options.MaxQueuedBytes = 32 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(15);
                    options.QueueOneWayCalls = true;
                });
            var memory = transport == "shared-memory" ? $"sharplink-n-delivery-{Guid.NewGuid():N}" : null;
            if (memory is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memory);
            var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memory, settings => settings.UseRuntime(ConfigureWindows));
            Ensure(server.RegisterAssembly(serverAssembly).Succeeded, "server publishes its real delivery service");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var session = NStudyClientSession(client);
            var retired = await ExerciseNStudyPreparedDeliveryAsync(server, client, service, directory, path, newAssembly,
                observer, trace, aCid, bCid, b, bBinding, memory, streamWindow, connectionWindow, ct)
                .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (var i = 0; retired.OldContext.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.OldContext.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retired.OldContext.IsAlive && ReferenceEquals(NStudyClientSession(client), session) &&
                   await b.TouchAsync(4, ct) == 104 &&
                   session.CaptureNStudyBinding(aCid) == retired.Ready.BindingId &&
                   session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
                "the replaced local context collects without dropping either current site on this Session");
            trace.Record("RetiredClientContextCollectsOnOriginalSession", retired.Ready, bBinding);
        }
        finally
        {
            newLoader.Unload();
            serverLoader.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference OldContext, NStudyAck Ready)> ExerciseNStudyPreparedDeliveryAsync(
        SharpLinkServer server, SharpLinkClient client, Type service, string directory, string path, Assembly newAssembly,
        NStudyUnarySiteObserver observer, NStudyTrace trace, long aCid, long bCid, IShutdownCleanupProbe b, NStudyAck bBinding,
        string? memory, int streamWindow, int connectionWindow, CancellationToken ct)
    {
        var oldLoader = new PluginLoadContext("delivery-client-r1", directory);
        var weak = new WeakReference(oldLoader);
        var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
        var oldInput = new NStudyInput();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Ensure(client.RegisterAssembly(oldAssembly).Succeeded, "the first real local generation registers");
            var oldModule = GetDynamicModule(client, oldAssembly);
            var oldCodec = FeedType(oldAssembly, "FeedReadingCodec");
            var newCodec = FeedType(newAssembly, "FeedReadingCodec");
            var serverCodec = FeedType(service.Assembly, "FeedReadingCodec");
            var old = CreateNStudyDelivery(client, oldAssembly, "generated-r1");
            var a1 = await old.Prepare(TimeSpan.FromSeconds(5), ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var bindSequence = session.NStudyBindOperationForDiagnostics;
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            var flow = ((RpcSessionProtocolState)typeof(RpcSession).GetField("_protocolState",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).FlowController!;
            Ensure(a1.Authority == bBinding.Authority && a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader) &&
                   session.NegotiatedOptions?.StreamReceiveWindowBytes == streamWindow &&
                   flow.SendConnectionCredit == connectionWindow, "auth and bounded windows are active on the physical Session");
            var blocked = old.WaitForApproval(ct).AsTask();
            await ReadNStudyTask(service, "WaitingForApproval").WaitAsync(ct);

            await old.PostGenerated(5, ct).AsTask().WaitAsync(ct);
            await old.PostManual(7, Metadata("manual-post"), ct).AsTask().WaitAsync(ct);
            await old.UploadGenerated(Values(3, 4), ct).AsTask().WaitAsync(ct);
            await WaitForNStudyAsync(() => kernel.QueuedCalls == 3 &&
                peer.Session.StreamManager.ActiveStreamCount == 1, ct);
            Ensure(!blocked.IsCompleted && FeedCount(service, "Posted") == 0 && FeedCount(service, "Uploaded") == 0 &&
                   FeedCount(oldCodec, "Serialized") == 2 && FeedCount(serverCodec, "Deserialized") == 0 &&
                   connection.PendingCalls.NStudyProducerCount == 0 && session.NStudyBindOperationForDiagnostics == bindSequence &&
                   await b.TouchAsync(1, ct) == 101,
                "plain and typed OneWay complete locally without waiting for remote handlers or a receipt");
            trace.Record("PlainAndTypedOneWayFinishLocallyBeforeRemoteAdmission", a1, bBinding);

            service.GetMethod("Approve")!.Invoke(null, null);
            Ensure(await blocked.WaitAsync(ct) == 42, "the occupying approval completes normally");
            await WaitForNStudyAsync(() => FeedCount(service, "Posted") == 12 && FeedCount(service, "Uploaded") == 7 &&
                kernel.ActivePermits == 0 && flow.SendConnectionCredit == connectionWindow, ct);
            Ensure(FeedCount(serverCodec, "Deserialized") == 2 &&
                   observer.Calls.Where(call => call.Method.Kind == RpcMethodKind.OneWay).Select(call => call.Label)
                       .Order().SequenceEqual(new[] { "generated-r1", "generated-r1", "manual-post" }),
                "queued generated and manual calls finally dispatch with their original metadata");
            trace.Record("QueuedGeneratedAndManualCallsDispatchWithOriginalMetadata", a1);
            server.DisableAdmissionControl();

            var running = old.UploadManual(oldInput, Metadata("manual-r1-input"), cancel.Token).AsTask();
            await oldInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => FeedCount(service, "Uploaded") == 10, ct);
            var requestId = peer.LastAcceptedRequestId;
            var sourceHandle = oldInput.ProducerToken.WaitHandle.SafeWaitHandle;
            Ensure(oldModule.RemainingStreams == 1 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   FeedCount(oldCodec, "Serialized") == 3, "original local module and its typed codec own the active producer");
            var replacing = client.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            Ensure(!replacing.IsCompleted && oldModule.State == SharpLinkDynamicModuleState.Draining,
                "replacement publishes while original physical local input remains live");
            var current = CreateNStudyDelivery(client, newAssembly, "generated-r2");
            var a2 = await current.Prepare(TimeSpan.FromSeconds(5), ct);
            await current.PostGenerated(11, ct).AsTask().WaitAsync(ct);
            await WaitForNStudyAsync(() => FeedCount(service, "Posted") == 23 &&
                peer.NStudyBindings.Snapshot() == (2, 1, 1), ct);
            Ensure(a2.Authority == a1.Authority && a2.Target == a1.Target && a2.BindingId != a1.BindingId &&
                   oldModule.RemainingStreams == 1 && FeedCount(oldCodec, "Disposed") == 0 &&
                   !sourceHandle.IsClosed && await b.TouchAsync(2, ct) == 102,
                "the fresh local site and B continue while the original writer keeps its exact owner");
            trace.Record("NewLocalSiteUsesNewBindWhileOldWriterOwnsItsModule", a1, a2);

            oldInput.ReleaseItems();
            await oldInput.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => FeedCount(service, "Uploaded") == 17, ct);
            cancel.Cancel();
            await WaitForNStudyAsync(() => !peer.CallCancellations.TryGetValue(requestId, out _), ct);
            Ensure(connection.PendingCalls.ActiveCount == 0 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   oldModule.RemainingStreams == 1 && !replacing.IsCompleted && !running.IsCompleted &&
                   FeedCount(oldCodec, "Serialized") == 4 && FeedCount(oldCodec, "Disposed") == 0 &&
                   FeedCount(newCodec, "Serialized") == 0 && observer.Calls.Any(call => call.Label == "manual-r1-input") &&
                   oldInput.ProducerToken.IsCancellationRequested && !sourceHandle.IsClosed && await b.TouchAsync(3, ct) == 103,
                "a post-cut item used the original codec; ordinary cancellation does not dispose it or the CTS during writer cleanup");
            trace.Record("OldTypedCodecSurvivesCutAndCallerCancellation", a1, a2);
            oldInput.ReleaseDisposal();
            await ExpectNStudyCallerCancellation(running, cancel.Token, ct);
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "client retires the original physical OneWay input");
            await WaitForNStudyAsync(() => connection.ActiveCallCount == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0) && peer.Session.StreamManager.ActiveStreamCount == 0 &&
                flow.SendConnectionCredit == connectionWindow && flow.ActiveSendStreamCount == 0, ct);
            Ensure(sourceHandle.IsClosed && oldInput.ExitCount == 1 && FeedCount(oldCodec, "Disposed") == 1 &&
                   FeedCount(newCodec, "Disposed") == 0 && FeedCount(serverCodec, "Deserialized") == 4 &&
                   ReferenceEquals(NStudyClientSession(client), session) &&
                   session.CaptureNStudyBinding(aCid) == a2.BindingId && session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
                "physical exit returns original CTS, codec scope and credit once while the current site is untouched");
            trace.Record("OriginalPhysicalTailReturnsCodecCreditAndBinding", a1, a2);
            return (weak, a2);
        }
        finally
        {
            oldInput.ReleaseItems();
            oldInput.ReleaseDisposal();
            service.GetMethod("Approve")!.Invoke(null, null);
            oldLoader.Unload();
        }
    }

    private static NStudyDeliveryCalls CreateNStudyDelivery(SharpLinkClient client, Assembly assembly, string generatedLabel)
        => (NStudyDeliveryCalls)typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(CreateNStudyTypedDelivery),
            BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(FeedType(assembly, "IDeliveryUpdates"), FeedType(assembly, "FeedReading"))
            .Invoke(null, [client, assembly, generatedLabel])!;

    private static NStudyDeliveryCalls CreateNStudyTypedDelivery<TContract, TReading>(
        SharpLinkClient client, Assembly assembly, string generatedLabel) where TContract : IService
    {
        var site = CreateNStudyPublicStreaming<TContract>(client, assembly, generatedLabel);
        var post = site.Method("PostAsync");
        var upload = site.Method("UploadAsync");
        var requestCodec = new NStudyManualIntRequestCodec();
        var itemCodec = site.Codecs.GetCodec<TReading>();
        return new(site.Prepare,
            ct => InvokeValueTaskAsync<int>(site.Service, typeof(TContract), "WaitForApprovalAsync", ct),
            (value, ct) => InvokeValueTaskAsync(site.Service, typeof(TContract), "PostAsync", value, ct),
            (value, metadata, ct) => site.Channel.InvokeOneWayAsync(post, value, requestCodec,
                default(RpcNoClientStreams), metadata, ct),
            (items, ct) => InvokeValueTaskAsync(site.Service, typeof(TContract), "UploadAsync", AsDeliveryReading<TReading>(items), ct),
            (items, metadata, ct) => site.Channel.InvokeOneWayAsync(upload, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                new NStudyManualDeliveryWriter<TReading>(AsDeliveryReading<TReading>(items), itemCodec), metadata, ct));
    }

    private sealed record NStudyDeliveryCalls(
        Func<TimeSpan, CancellationToken, ValueTask<NStudyAck>> Prepare,
        Func<CancellationToken, ValueTask<int>> WaitForApproval,
        Func<int, CancellationToken, ValueTask> PostGenerated,
        Func<int, SharpLinkMetadata, CancellationToken, ValueTask> PostManual,
        Func<IAsyncEnumerable<int>, CancellationToken, ValueTask> UploadGenerated,
        Func<IAsyncEnumerable<int>, SharpLinkMetadata, CancellationToken, ValueTask> UploadManual);

    private readonly struct NStudyManualDeliveryWriter<T>(IAsyncEnumerable<T> items, IRpcCodec<T> codec) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
            => new(sink.SendClientStreamAsync(requestId, 1, items, codec, cancellationToken));
    }

    private static async IAsyncEnumerable<T> AsDeliveryReading<T>(IAsyncEnumerable<int> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var value in items.WithCancellation(ct))
            yield return (T)Activator.CreateInstance(typeof(T), value)!;
    }
}
