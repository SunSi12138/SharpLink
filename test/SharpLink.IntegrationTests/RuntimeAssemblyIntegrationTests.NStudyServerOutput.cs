using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyServerOutputCancellationKeepsOriginalCleanupOwner(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-output-{transport}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "generated contract manifests load");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-output-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName);
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        Type? oldType = null;
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            oldType.GetMethod("HoldServerOutputResource")!.Invoke(null, null);
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "R1 and real generated contract register");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var bBinding = await client.PrepareNStudyContractAsync(bCid, staticManifest.RpcAssemblyHash, default, ct);
            var a1 = await client.PrepareNStudyContractAsync(aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(oldAssembly, aCid), ct);
            var connection = NStudyClientConnection(client);
            var peer = NStudyServerConnection(server);
            Ensure(a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? connection.Session.RemoteEndPoint is IPEndPoint :
                       connection.Session.Input is SharedMemoryPipeReader), "auth and actual transport are active");
            var a = GetProxy(client, contracts.ContractType);
            var b = client.Get<IShutdownCleanupProbe>();
            var oldModule = GetDynamicModule(server, oldAssembly);
            var oldOutput = InvokeStream(a, contracts.ContractType, "ServerStreamAsync", 3, cancel.Token);
            await using var reader = oldOutput.GetAsyncEnumerator();
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(ct) && reader.Current == 0,
                "the unmodified generated ServerStreaming path delivers its first item");
            await ReadNStudyTask(oldType, "ServerOutputAwaitingItem").WaitAsync(ct);
            var requestId = peer.LastAcceptedRequestId;
            Ensure(peer.CallCancellations.TryGetValue(requestId, out var originalCall) &&
                   oldModule.RemainingStreams == 1 && connection.PendingCalls.NStudyProducerCount == 0,
                "standalone output keeps one original server owner and no client producer");
            var originalToken = originalCall!.InvocationToken;
            var originalHandle = originalToken.WaitHandle.SafeWaitHandle;
            trace.Record("OriginalServerStreamDeliversFirstItem", a1);

            var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            await WaitForNStudyAsync(() => connection.Session.NStudyPreparedCount == 1, ct);
            var a2 = await client.PrepareNStudyContractAsync(aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(newAssembly, aCid), ct);
            var r2 = await CollectAsync(InvokeStream(a, contracts.ContractType, "ServerStreamAsync", 3, ct)).WaitAsync(ct);
            Ensure(r2.SequenceEqual(new[] { 0, 1, 2 }) && NStudyCount(newType, "Created") == 1 &&
                   a2.Authority == a1.Authority && a2.Target != a1.Target &&
                   NStudyWire.SlotIndex(a2.BindingId) != NStudyWire.SlotIndex(a1.BindingId) &&
                   connection.Session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await b.TouchAsync(5, ct) == 105, "new R2 stream and B continue on the original Session");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 1, 1), ct);
            trace.Record("NewServerStreamAndBFinishWhileOriginalWaits", a1, a2);

            var next = reader.MoveNextAsync().AsTask();
            cancel.Cancel();
            await ExpectNStudyCallerCancellation(next, cancel.Token, ct);
            await ReadNStudyTask(oldType, "ServerOutputCleanupStarted").WaitAsync(ct);
            Ensure(!replacing.IsCompleted && peer.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   oldModule.RemainingStreams == 1 && NStudyCount(oldType, "Disposed") == 0 &&
                   NStudyCount(oldType, "ServerOutputCleanupCount") == 0 &&
                   peer.CallCancellations.TryGetValue(requestId, out var retainedCall) &&
                   ReferenceEquals(retainedCall, originalCall) && retainedCall.InvocationToken == originalToken &&
                   originalToken.IsCancellationRequested && !originalHandle.IsClosed &&
                   await b.TouchAsync(6, ct) == 106,
                "caller sees cancellation before physical server cleanup; original CTS, binding and module stay held");
            trace.Record("ServerCancellationKeepsOriginalBindingAndCtsDuringCleanup", a1, a2);

            oldType.GetMethod("FinishServerOutputCleanup")!.Invoke(null, null);
            await ReadNStudyTask(oldType, "ServerStreamDisposed").WaitAsync(ct);
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "old server output ends physically");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0) &&
                peer.ActiveCalls == 0 && connection.ActiveCallCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 &&
                connection.Session.StreamManager.ActiveStreamCount == 0, ct);
            Ensure(NStudyCount(oldType, "ServerOutputCleanupCount") == 1 && NStudyCount(oldType, "Disposed") == 1 &&
                   !peer.CallCancellations.TryGetValue(requestId, out _) && originalHandle.IsClosed &&
                   ReferenceEquals(connection.Session, NStudyClientSession(client)),
                "original asynchronous cleanup and module disposal run once; routes and CTS reclaim");
            trace.Record("OriginalServerCleanupExitsOnceOnTheSameSession", a1, a2);
        }
        finally
        {
            oldType?.GetMethod("FinishServerOutputCleanup")!.Invoke(null, null);
            oldType?.GetMethod("ReleaseServerOutput")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static Task ReadNStudyTask(Type service, string name)
        => (Task)service.GetProperty(name)!.GetValue(null)!;
}
