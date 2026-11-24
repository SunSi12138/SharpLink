using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client-stream")]
    [Arguments("tcp", "duplex")]
    [Arguments("shared-memory", "client-stream")]
    [Arguments("shared-memory", "duplex")]
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
            var duplex = shape == "duplex";
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
            Ensure(queuedInput.Enumerators == 0 && FeedCount(oldCodec, "Serialized") == 0 &&
                   FeedCount(serverCodec, "Deserialized") == 0 && !queued.IsCompleted && serverModule.RemainingStreams == 0 &&
                   await b.TouchAsync(1, ct) == 101, "the real queued head has no assembled producer or item before acceptance");
            trace.Record("PreparedTwoWayDoesNotOpenQueuedProducer", a1, bBinding);

            service.GetMethod("Approve")!.Invoke(null, null);
            Ensure(await occupying.WaitAsync(ct) == 42, "the ordinary approval releases one server permit");
            await queuedInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => FeedCount(service, "Reconciled") == 3, ct);
            Ensure(queuedInput.Enumerators == 1 && oldModule.RemainingStreams == 2 &&
                   serverModule.RemainingStreams == 1 && FeedCount(oldCodec, "Serialized") == 1,
                "the single accepted generated producer uses the exact typed Codec and independent local ownership");
            trace.Record("RequestAcceptedStartsGeneratedTypedProducer", a1);
            queuedInput.ReleaseItems();
            queuedInput.ReleaseDisposal();
            Ensure((await queued.WaitAsync(ct)).SequenceEqual(duplex ? [6, 14] : [10]),
                "normal generated typed output contains exactly the accepted inputs");
            await WaitForNStudyAsync(() => kernel.ActivePermits == 0 && serverModule.RemainingStreams == 0 &&
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
            Generated, Manual);
    }

    private sealed record NStudyPreparedTwoWay(
        Func<TimeSpan, CancellationToken, ValueTask<NStudyAck>> Prepare,
        Func<CancellationToken, ValueTask<int>> Occupy,
        Func<IAsyncEnumerable<int>, CancellationToken, Task<int[]>> Generated,
        Func<IAsyncEnumerable<int>, SharpLinkMetadata, CancellationToken, Task<int[]>> Manual);
}
