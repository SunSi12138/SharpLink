using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyServerQueriesActualClientDeploymentAcrossReplacementAndRefresh(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var oldLoader = new PluginLoadContext($"client-inventory-r1-{transport}", directory);
        var newLoader = new PluginLoadContext($"client-inventory-r2-{transport}", directory);
        var seq = 0;
        void Record(string edge, NStudyInventorySnapshot snapshot, NStudyObservedTarget selected)
            => Console.WriteLine("NSTUDY " + JsonSerializer.Serialize(new
            {
                seq = ++seq, transport, edge, revision = snapshot.Revision, entries = snapshot.Entries.Count,
                publisher = snapshot.PublisherEpoch, epoch = snapshot.Authority.RegistryEpoch,
                session = snapshot.Authority.PhysicalSession, registration = selected.Target.Registration,
                publication = selected.Target.Publication
            }));

        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
            var oldLoad = SharpLinkAssemblyManifestLoader.TryLoad(oldAssembly, out var oldManifest);
            var newLoad = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, out var newManifest);
            Ensure(staticLoad.Succeeded && oldLoad.Succeeded && newLoad.Succeeded, "genuine generated manifests load");
            var aCid = oldManifest!.Contracts.Single(c => c.ContractType == FeedType(oldAssembly, "IReadingFeed")).ContractId;
            var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([staticManifest]);
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider);
            var memory = transport == "shared-memory" ? $"sharplink-n-client-inventory-{Guid.NewGuid():N}" : null;
            if (memory is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memory);
            var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memory);
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var session = NStudyClientSession(client);
            var peer = NStudyServerConnection(server);

            // Both endpoints independently issue their first read-only query, before any Bind.
            var fromServer = server.QueryNStudyClientInventoryAsync(peer, ct);
            var fromClient = client.QueryNStudyRemoteInventoryAsync(ct);
            var initial = await fromServer;
            var initialRemote = await fromClient;
            var b = initial.Select(bCid);
            Ensure(initial.Status == NStudyInventoryStatus.Current && initial.Authority == initialRemote.Authority &&
                   initial.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   initial.PublisherEpoch != initialRemote.PublisherEpoch &&
                   initialRemote.PublisherEpoch == initialRemote.Authority.RegistryEpoch &&
                   initial.Entries.All(entry => entry.Contract != aCid) && initial.Revision == 0 &&
                   session.NStudyPreparedCount == 0 && peer.NStudyBindings.Snapshot() == (0, 0, 0) &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "bilateral reads on the real authenticated Ready Session have independent publishers and create no binding");
            Record("BothDirectionsReadOwnPublisherWithoutBinding", initial, b);

            Ensure(client.RegisterAssembly(oldAssembly).Succeeded, "normal client R1 deployment");
            var r1 = await server.QueryNStudyClientInventoryAsync(peer, ct);
            var a1 = r1.Select(aCid);
            Ensure(r1.PublisherEpoch == initial.PublisherEpoch && r1.Authority == initial.Authority &&
                   r1.Revision > initial.Revision && r1.Select(bCid) == b && a1.Hash == oldManifest.RpcAssemblyHash &&
                   a1.Target.Registration == GetDynamicModule(client, oldAssembly).NStudyRegistration &&
                   (await client.QueryNStudyRemoteInventoryAsync(ct)).Entries.All(entry => entry.Contract != aCid),
                "a client-local deployment is observed with its own hash and identity; the server has not deployed A");
            Record("ServerObservesActualClientOnlyR1", r1, a1);

            EnsureReplacementReleased(await client.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(10), ct),
                "normal client-local R1 to R2 replacement");
            var r2 = await server.QueryNStudyClientInventoryAsync(peer, ct);
            var a2 = r2.Select(aCid);
            Ensure(r2.PublisherEpoch == r1.PublisherEpoch && r2.Authority == r1.Authority &&
                   r2.Revision > r1.Revision && r2.Select(bCid) == b && a2.Hash == a1.Hash &&
                   a2.Hash == newManifest!.RpcAssemblyHash && a2.Target.Registration != a1.Target.Registration &&
                   a2.Target.Publication > a1.Target.Publication &&
                   a2.Target.Registration == GetDynamicModule(client, newAssembly).NStudyRegistration &&
                   !client.IsDynamicAssemblyRegistered(oldAssembly),
                "same-hash replacement changes the exact client registration without changing unaffected B");
            Record("ClientReplacementAdvancesExactRegistrationWithoutChangingB", r2, a2);

            await server.PublishDesiredSessionAsync(new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = 128 * 1024
            }, SharpLinkSessionRolloutMode.RollingRefresh, ct);
            await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 &&
                !ReferenceEquals(session, NStudyClientSession(client)), ct);
            var fresh = NStudyClientSession(client);
            var freshPeer = NStudyServerConnection(server);
            var afterRefresh = await server.QueryNStudyClientInventoryAsync(freshPeer, ct);
            var freshA = afterRefresh.Select(aCid);
            Ensure(afterRefresh.Authority.PhysicalSession == freshPeer.NStudyPhysicalIdentity &&
                   afterRefresh.Authority.PhysicalSession != r2.Authority.PhysicalSession &&
                   afterRefresh.Authority.Server == r2.Authority.Server &&
                   afterRefresh.Authority.RegistryEpoch == r2.Authority.RegistryEpoch &&
                   afterRefresh.PublisherEpoch == r2.PublisherEpoch && afterRefresh.Revision == r2.Revision &&
                   freshA.Target == a2.Target && afterRefresh.Select(bCid).Target == b.Target &&
                   fresh.NStudyBindOperationForDiagnostics == 0 && fresh.NStudyPreparedCount == 0 &&
                   freshPeer.NStudyBindings.Snapshot() == (0, 0, 0),
                "fresh reverse query rediscovers deployment but brings no Session permission forward");
            Record("FreshSessionRediscoversClientDeploymentWithoutGrants", afterRefresh, freshA);
        }
        finally
        {
            oldLoader.Unload();
            newLoader.Unload();
        }
    }
}
