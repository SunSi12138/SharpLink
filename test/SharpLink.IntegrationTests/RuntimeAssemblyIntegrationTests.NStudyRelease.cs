using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyPrepareReportsBindingCapacityAndRecoversWithoutDisconnect(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Automatic, twoPeers: false,
            async (fixture, _, ct) =>
            {
                var peer = fixture.First;
                var table = peer.ServerConnection.NStudyBindings;
                Ensure(table.TryResolve(peer.A.BindingId, out var registration), "the prepared service exists");
                Ensure(await peer.Client.ReleaseNStudyContractAsync(peer.A.Contract, ct),
                    "release A so its next Prepare actually sends a Bind");
                await WaitForNStudyAsync(() => table.Snapshot() == (1, 0, 0), ct);

                // Fill the bounded server table directly. The probe then uses only
                // normal public Prepare and ordinary business calls on the real Session.
                var held = new List<long>(NStudyWire.BindingLimit - 1);
                try
                {
                    for (var i = 1; i < NStudyWire.BindingLimit; i++)
                    {
                        Ensure(table.TryInstall(registration, out var binding), "fill one binding slot");
                        held.Add(binding);
                    }

                    var local = peer.Client.GetLocalContract(fixture.Contracts.ContractAssembly, peer.A.Contract);
                    var channel = peer.Client.CreateContractChannel(local);
                    try
                    {
                        await channel.PrepareAsync(TimeSpan.FromSeconds(5), ct);
                        throw new InvalidOperationException("A full binding table must reject Prepare.");
                    }
                    catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted)
                    {
                        Ensure(error.DetailCode == SharpLinkErrorDetails.ResourceExhausted.PreparedBindingCapacity,
                            "capacity rejection is distinguishable from a busy preparation control operation");
                    }
                    Ensure(await peer.Sibling.TouchAsync(2, ct) == 102 && table.Snapshot() == (NStudyWire.BindingLimit, 0, 0),
                        "the existing unrelated binding stays usable and the rejection reserves nothing");
                    table.Release(held[^1]);
                    held.RemoveAt(held.Count - 1);
                    await channel.PrepareAsync(TimeSpan.FromSeconds(5), ct);
                    Ensure(await InvokeValueTaskAsync<int>(peer.Receiver.Service, fixture.Contracts.ContractType,
                               "UnaryAsync", 8, ct) == 9 &&
                           ReferenceEquals(NStudyClientSession(peer.Client), peer.Session),
                        "freeing a slot allows the public Prepare to succeed on the original connection");
                }
                finally
                {
                    foreach (var binding in held)
                        table.Release(binding);
                }
            });

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyReleaseShouldDrainBeforeReuseAndRebindAfterRefresh(string transport)
    {
        const int frameLimit = 256 * 1024;
        const int reuseCycles = NStudyWire.BindingLimit + 4;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        using var contracts = PluginBundle.Load($"n-release-{transport}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var contractLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && contractLoad.Succeeded, "regular generated manifests load");
        var src = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        try
        {
            string? memoryName = transport == "shared-memory" ? $"sharplink-n-release-{Guid.NewGuid():N}" : null;
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(src, provider);
            if (memoryName is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memoryName);
            var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(src, port, memoryName);
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "same explicit contract on each endpoint");
            var old = oldLoader.LoadFromAssemblyPath(path);
            var next = newLoader.LoadFromAssemblyPath(path);
            var oldType = old.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var nextType = next.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var registered = server.RegisterAssembly(old);
            Ensure(registered.Succeeded, $"R1 is installed: {registered.Error}");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var first = NStudyClientSession(client);
            Ensure((memoryName is null ? first.RemoteEndPoint is IPEndPoint : first.Input is SharedMemoryPipeReader) &&
                   first.NegotiatedMaxFramePayloadBytes > frameLimit &&
                   (first.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0,
                "genuine configured transport with the regular rolling refresh capability");
            var timeout = TimeSpan.FromSeconds(5);
            var sibling = client.CreateProxy<IShutdownCleanupProbe>();
            var b = sibling.Service;
            await sibling.PrepareAsync(timeout, ct);
            var b1 = first.CaptureNStudyRoute(bCid).Acknowledgement;
            var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
            var a = receiver.Service;
            var r1 = server.ReadNStudyTarget(old, aCid);
            var firstA = await receiver.PrepareExact(new(b1.Authority, aCid, manifest.RpcAssemblyHash, r1), timeout, ct);
            var registry = (ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            var original = registry.SnapshotActive().Single();
            Ensure(b1.Authority == firstA.Authority && b1.Hash == staticManifest.RpcAssemblyHash &&
                   original.NStudyPhysicalIdentity == firstA.Authority.PhysicalSession &&
                   original.AuthenticationContext?.Subject == "n-validation", "first actual Session authenticated");
            var originalCall = InvokeValueTaskAsync<int>(a, contracts.ContractType, "BlockAsync", ct).AsTask();
            try
            {
                await ((Task)oldType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
                Ensure(await client.ReleaseNStudyContractAsync(aCid, ct), "regular local A release completes its send");
                Ensure(await b.TouchAsync(1, ct) == 101 && first.NStudyPreparedCount == 1 &&
                       first.CaptureNStudyBinding(bCid) == b1.BindingId,
                    "unrelated B remains bound and the actual Release preceding it is processed");
                await WaitForNStudyAsync(() => original.NStudyBindings.Snapshot() == (1, 1, 1), ct);
                RecordNStudyRelease(transport, "ReleasedWithOriginalUseStillHeld", firstA, original, 0);

                var replacing = server.ReplaceAssemblyAsync(old, next, TimeSpan.FromSeconds(15), ct).AsTask();
                var r2 = server.ReadNStudyTarget(next, aCid);
                var targetOnFirst = new NStudyObservedTarget(firstA.Authority, aCid, manifest.RpcAssemblyHash, r2);
                var selected = await receiver.PrepareExact(targetOnFirst, timeout, ct);
                Ensure(!originalCall.IsCompleted && !replacing.IsCompleted && selected.Authority == firstA.Authority &&
                       selected.Target == r2 && SlotOf(selected.BindingId) != SlotOf(firstA.BindingId),
                    "new A must not reuse the retiring slot while its original reader is active");
                Ensure(await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 6, ct) == 7 &&
                       NStudyCount(nextType, "Created") == 1 && NStudyCount(oldType, "Disposed") == 0,
                    "generated R2 call runs while the old R1 owner stays alive");
                await WaitForNStudyAsync(() => original.NStudyBindings.Snapshot() == (2, 1, 1), ct);
                RecordNStudyRelease(transport, "ReplacementUsesDifferentSlotDuringDrain", selected, original, 0);

                oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
                Ensure(await originalCall.WaitAsync(ct) == 42, "original R1 returns normally through its original route");
                EnsureReplacementReleased(await replacing.WaitAsync(ct), "original R1 physical retirement");
                Ensure(NStudyCount(oldType, "Disposed") == 1, "exact R1 disposal once");
                await WaitForNStudyAsync(() => original.NStudyBindings.Snapshot() == (2, 0, 0), ct);
                Ensure(await client.ReleaseNStudyContractAsync(aCid, ct), "normal R2 binding release");
                selected = await receiver.PrepareExact(targetOnFirst, timeout, ct);
                Ensure(SlotOf(selected.BindingId) == SlotOf(firstA.BindingId) &&
                       IncarnationOf(selected.BindingId) > IncarnationOf(firstA.BindingId),
                    "the original slot is reused only after physical completion, with a different generation");
                RecordNStudyRelease(transport, "PhysicalExitMakesOriginalSlotReusable", selected, original, 0);

                for (var i = 0; i < reuseCycles; i++)
                {
                    var prior = selected;
                    Ensure(await client.ReleaseNStudyContractAsync(aCid, ct), "release each normally prepared A");
                    selected = await receiver.PrepareExact(targetOnFirst, timeout, ct);
                    Ensure(SlotOf(selected.BindingId) == SlotOf(prior.BindingId) &&
                           IncarnationOf(selected.BindingId) > IncarnationOf(prior.BindingId) &&
                           selected.Target == r2, "rebind reuses capacity and advances the complete token");
                }
                Ensure(await b.TouchAsync(2, ct) == 102 && ReferenceEquals(first, NStudyClientSession(client)) &&
                       first.CaptureNStudyBinding(bCid) == b1.BindingId &&
                       await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 8, ct) == 9,
                    "normal cold reuse neither replaces the Session nor touches B");
                await WaitForNStudyAsync(() => original.NStudyBindings.Snapshot() == (2, 0, 0), ct);
                RecordNStudyRelease(transport, "MoreThanTableCapacitySuccessfulRebinds", selected, original, reuseCycles);

                await server.PublishDesiredSessionAsync(new SharpLinkServerDesiredSessionConfiguration
                {
                    MaxFramePayloadBytes = frameLimit
                }, SharpLinkSessionRolloutMode.RollingRefresh, ct);
                await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 &&
                    !ReferenceEquals(first, NStudyClientSession(client)), ct);
                var fresh = NStudyClientSession(client);
                Ensure(fresh.NegotiatedMaxFramePayloadBytes == frameLimit && fresh.NStudyPreparedCount == 0,
                    "fresh Session negotiates the new limit without inheriting old grants");
                await sibling.PrepareAsync(timeout, ct);
                var b2 = fresh.CaptureNStudyRoute(bCid).Acknowledgement;
                var a2 = await receiver.PrepareExact(new(b2.Authority, aCid, manifest.RpcAssemblyHash, r2), timeout, ct);
                var connected = registry.SnapshotActive().Single(p => p.NStudyPhysicalIdentity == a2.Authority.PhysicalSession);
                Ensure(a2.Authority == b2.Authority && a2.Authority.PhysicalSession != firstA.Authority.PhysicalSession &&
                       a2.Authority.Server == firstA.Authority.Server && a2.Authority.RegistryEpoch == firstA.Authority.RegistryEpoch &&
                       connected.AuthenticationContext?.Subject == "n-validation" &&
                       a2.BindingId == firstA.BindingId && a2.Target == r2,
                    "a new authenticated Session independently binds current R2 even when its numeric token repeats");
                Ensure(await b.TouchAsync(3, ct) == 103 &&
                       await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 10, ct) == 11,
                    "same generated proxies use freshly bound current endpoints");
                await InvokeValueTaskAsync(a, contracts.ContractType, "NotifyAsync", 15, ct).AsTask().WaitAsync(ct);
                await WaitForNStudyAsync(() => NStudyCount(nextType, "Notifications") == 15 &&
                    !first.IsConnected && registry.SnapshotActive().All(p => !ReferenceEquals(p, original)), ct);
                RecordNStudyRelease(transport, "RefreshedSessionRebindsCurrentR2", a2, connected, 0);
            }
            finally
            {
                oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            }
        }
        finally
        {
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static uint SlotOf(long token) => (uint)token;
    private static uint IncarnationOf(long token) => (uint)((ulong)token >> 32);

    private static void RecordNStudyRelease(string transport, string edge, NStudyAck ack,
        ServerConnectionState peer, int cycles)
    {
        var bindingCounts = peer.NStudyBindings.Snapshot();
        Console.WriteLine("NRELEASE " + JsonSerializer.Serialize(new
        {
            transport, edge, session = ack.Authority.PhysicalSession, slot = SlotOf(ack.BindingId),
            incarnation = IncarnationOf(ack.BindingId), registration = ack.Target.Registration,
            publication = ack.Target.Publication, cycles,
            open = bindingCounts.Open, retiring = bindingCounts.Retiring, uses = bindingCounts.Users
        }));
    }
}
