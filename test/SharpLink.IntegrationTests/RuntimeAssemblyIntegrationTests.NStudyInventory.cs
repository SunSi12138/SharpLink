using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyInventoryCanPrepareExactCurrentRegistrationAfterReplacementAndRefresh(string transport)
    {
        const int refreshedFrameLimit = 128 * 1024;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var localLoader = new PluginLoadContext($"inventory-client-{transport}", directory);
        var oldLoader = new PluginLoadContext($"inventory-server-r1-{transport}", directory);
        var newLoader = new PluginLoadContext($"inventory-server-r2-{transport}", directory);
        var seq = 0;
        void Record(string edge, NStudyInventorySnapshot snapshot, NStudyObservedTarget? selected = null, NStudyAck? bound = null)
            => Console.WriteLine("NSTUDY " + JsonSerializer.Serialize(new
            {
                seq = ++seq,
                transport,
                edge,
                revision = snapshot.Revision,
                entries = snapshot.Entries.Count,
                server = snapshot.Authority.Server,
                epoch = snapshot.Authority.RegistryEpoch,
                session = snapshot.Authority.PhysicalSession,
                registration = selected?.Target.Registration,
                publication = selected?.Target.Publication,
                binding = bound?.BindingId
            }));
        try
        {
            var localAssembly = localLoader.LoadFromAssemblyPath(path);
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            FeedType(newAssembly, "ReadingFeed").GetProperty("FirstValue")!.SetValue(null, 20);
            var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
            var localLoad = SharpLinkAssemblyManifestLoader.TryLoad(localAssembly, out var localManifest);
            Ensure(staticLoad.Succeeded && localLoad.Succeeded, "the actual local generated manifests load");
            var aCid = localManifest!.Contracts.Single(c => c.ContractType == FeedType(localAssembly, "IReadingFeed")).ContractId;
            var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([staticManifest]);
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider);
            var memory = transport == "shared-memory" ? $"sharplink-n-inventory-{Guid.NewGuid():N}" : null;
            if (memory is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memory);
            var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memory);
            Ensure(client.RegisterAssembly(localAssembly).Succeeded, "the client already has the genuine target asset");
            var feed = BuildNStudyPreparedFeed(client, localAssembly, "inventory-feed");
            var bSite = client.CreateNStudyContractSite<IShutdownCleanupProbe>();
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var firstSession = NStudyClientSession(client);
            var firstPeer = NStudyServerConnection(server);
            var baseline = await client.QueryNStudyRemoteInventoryAsync(ct);
            var b1 = baseline.Select(bCid);
            Ensure(baseline.Authority.PhysicalSession == firstPeer.NStudyPhysicalIdentity &&
                   firstPeer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? firstSession.RemoteEndPoint is IPEndPoint : firstSession.Input is SharedMemoryPipeReader) &&
                   baseline.Entries.All(e => e.Contract != aCid) && firstSession.NStudyPreparedCount == 0 &&
                   firstSession.NStudyBindOperationForDiagnostics == 0,
                "initial real status query has this Session's authority and creates no binding");
            Record("InitialInventoryOnAuthenticatedSessionIsReadOnly", baseline, b1);
            var bBound = await bSite.PrepareExactAsync(b1, TimeSpan.FromSeconds(5), ct);

            var broadcastHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var legacyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var legacyReady = client.WaitForRemoteContractAsync(aCid, localManifest.RpcAssemblyHash, legacyCancellation.Token).AsTask();
            server.ContractManifestPublishBarrierForTesting = () =>
            {
                broadcastHeld.TrySetResult();
                return publish.Task;
            };
            NStudyInventorySnapshot replaced;
            NStudyObservedTarget r2;
            try
            {
                var registered = server.RegisterAssembly(oldAssembly);
                Ensure(registered.Succeeded, $"normal R1 registration: {registered.Error}");
                await broadcastHeld.Task.WaitAsync(ct);
                var first = await client.QueryNStudyRemoteInventoryAsync(ct);
                var r1 = first.Select(aCid);
                Ensure(!legacyReady.IsCompleted && first.Revision > baseline.Revision && first.Authority == baseline.Authority &&
                       first.Select(bCid) == b1 && r1.Hash == localManifest.RpcAssemblyHash &&
                       r1.Target == server.ReadNStudyTarget(oldAssembly, aCid) && firstSession.NStudyPreparedCount == 1,
                    "a real inventory response sees the new exact registration while the pushed Manifest is still old");
                Record("InventorySeesR1BeforeOrdinaryManifestBroadcast", first, r1);
                var a1 = await feed.PrepareExact(r1, TimeSpan.FromSeconds(5), ct);
                Ensure(a1.Target == r1.Target && a1.Authority == r1.Authority && !legacyReady.IsCompleted &&
                       (await CollectAsync(feed.Generated(false, ct)).WaitAsync(ct)).SequenceEqual(new[] { 10 }) &&
                       await bSite.Service.TouchAsync(1, ct) == 101,
                    "the exact prepared site reaches actual R1 without using the lagging legacy Manifest");
                Record("ObservedR1PreparesTheActualService", first, r1, a1);

                EnsureReplacementReleased(await server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(10), ct),
                    "normal R1 to R2 replacement");
                await WaitForNStudyAsync(() => firstSession.NStudyPreparedCount == 1, ct);
                var operation = firstSession.NStudyBindOperationForDiagnostics;
                try
                {
                    _ = await feed.PrepareExact(r1, TimeSpan.FromSeconds(5), ct);
                    throw new InvalidOperationException("An ordinary stale observed target should not prepare the new implementation.");
                }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
                Ensure(firstSession.NStudyBindOperationForDiagnostics == operation + 1 &&
                       firstPeer.NStudyBindings.Snapshot() == (1, 0, 0) &&
                       firstSession.CaptureNStudyBinding(bCid) == bBound.BindingId &&
                       await bSite.Service.TouchAsync(2, ct) == 102,
                    "an obsolete inventory observation is declined normally by the exact remote registration check");
                replaced = await client.QueryNStudyRemoteInventoryAsync(ct);
                r2 = replaced.Select(aCid);
                var a2 = await feed.PrepareExact(r2, TimeSpan.FromSeconds(5), ct);
                Ensure(replaced.Revision > first.Revision && replaced.Authority == first.Authority &&
                       replaced.Select(bCid) == b1 && r2.Hash == r1.Hash && r2.Target != r1.Target &&
                       r2.Target == server.ReadNStudyTarget(newAssembly, aCid) && a2.Target == r2.Target &&
                       !legacyReady.IsCompleted && (await CollectAsync(feed.Manual(false, Metadata("inventory-r2"), ct))
                           .WaitAsync(ct)).SequenceEqual(new[] { 20 }),
                    "re-query on the same Session selects only R2; B and the unchanged hash did not retarget an obsolete receipt");
                Record("RequeryAfterNormalReplacementSelectsOnlyR2", replaced, r2, a2);
            }
            finally
            {
                publish.TrySetResult();
                server.ContractManifestPublishBarrierForTesting = null;
            }
            await legacyReady.WaitAsync(ct);

            await server.PublishDesiredSessionAsync(new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = refreshedFrameLimit
            }, SharpLinkSessionRolloutMode.RollingRefresh, ct);
            await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 &&
                !ReferenceEquals(firstSession, NStudyClientSession(client)), ct);
            var fresh = NStudyClientSession(client);
            var afterRefresh = await client.QueryNStudyRemoteInventoryAsync(ct);
            var freshA = afterRefresh.Select(aCid);
            var freshB = afterRefresh.Select(bCid);
            try
            {
                _ = await feed.PrepareExact(r2, TimeSpan.FromSeconds(5), ct);
                throw new InvalidOperationException("A real observation from the previous Session should not be borrowed.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition) { }
            Ensure(afterRefresh.Authority.Server == replaced.Authority.Server &&
                   afterRefresh.Authority.RegistryEpoch == replaced.Authority.RegistryEpoch &&
                   afterRefresh.Authority.PhysicalSession != replaced.Authority.PhysicalSession &&
                   afterRefresh.Revision == replaced.Revision && freshA.Target == r2.Target && freshB.Target == b1.Target &&
                   fresh.NegotiatedMaxFramePayloadBytes == refreshedFrameLimit &&
                   fresh.NStudyPreparedCount == 0 && fresh.NStudyBindOperationForDiagnostics == 0,
                "rolling refresh yields a fresh physical authority, no old grants or borrowed observation");
            Record("RefreshRequiresObservationFromTheFreshPhysicalSession", afterRefresh, freshA);
            var freshBound = await feed.PrepareExact(freshA, TimeSpan.FromSeconds(5), ct);
            var freshBStrong = await bSite.PrepareExactAsync(freshB, TimeSpan.FromSeconds(5), ct);
            Ensure(freshBound.Authority == afterRefresh.Authority && freshBStrong.Authority == afterRefresh.Authority &&
                   freshBound.Target == r2.Target && (await CollectAsync(feed.Generated(false, ct)).WaitAsync(ct)).SequenceEqual(new[] { 20 }) &&
                   await bSite.Service.TouchAsync(3, ct) == 103 && fresh.NStudyPreparedCount == 2,
                "both retained generated sites use independently prepared exact targets on the refreshed Session");
            Record("FreshObservationRestoresExactAAndB", afterRefresh, freshA, freshBound);
        }
        finally
        {
            localLoader.Unload();
            oldLoader.Unload();
            newLoader.Unload();
        }
    }
}
