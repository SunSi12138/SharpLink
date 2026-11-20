using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "not-deployed")]
    [Arguments("tcp", "different-hash")]
    [Arguments("shared-memory", "not-deployed")]
    [Arguments("shared-memory", "different-hash")]
    public Task NStudyAutomaticRequiresAnActuallyCompatibleLocalDeployment(string transport, string startingAsset)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Automatic, twoPeers: false,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _) = fixture;
                var a1 = first.A;
                var a = GetProxy(first.Client, contracts.ContractType);
                var b = first.Client.Get<IShutdownCleanupProbe>();
                Ensure(await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 2, ct) == 3,
                    "initial compatible application calls exact R1");
                using var alternate = startingAsset == "different-hash"
                    ? PluginBundle.Load($"n-other-hash-{transport}", loadService: false, replacement: true) : null;
                if (alternate is not null)
                {
                    var result = await first.Client.ReplaceAssemblyAsync(contracts.ContractAssembly,
                        alternate.ContractAssembly, TimeSpan.FromSeconds(5), ct);
                    EnsureReplacementReleased(result, "application switches to its other real generated contract");
                    var actual = GetDynamicModule(first.Client, alternate.ContractAssembly).Manifest;
                    Ensure(actual.RpcAssemblyHash != a1.Hash &&
                           actual.Contracts.Any(item => item.ContractId == a1.Contract),
                        "separate generated compatibility fixture has the same CID and a different wire hash");
                }
                else
                {
                    var removed = await first.Client.UnregisterAssemblyAsync(contracts.ContractAssembly,
                        TimeSpan.FromSeconds(5), ct);
                    Ensure(removed.ReferencesReleased && !first.Client.IsDynamicAssemblyRegistered(contracts.ContractAssembly),
                        "application actually removed the compatible contract; no local target is loaded");
                }
                trace.Record("ClientLocalAssetsNoLongerMatchTheRemoteCandidate", a1);

                var declinedClient = first.Client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, approveLocally: null, ct);
                var declinedServer = server.StageNStudyReplacementAsync(first.ServerConnection, oldAssembly,
                    newAssembly, a1.Contract, NStudyInitiator.Server, approveLocally: null, TimeSpan.FromSeconds(10), ct);
                Ensure(await declinedClient.WaitAsync(ct) is null, "Automatic does not invent a compatible local asset");
                var declined = await declinedServer.WaitAsync(ct);
                var reason = alternate is null
                    ? NStudyDeclineReason.LocalAssetMissing : NStudyDeclineReason.LocalAssetIncompatible;
                Ensure(!declined.Replacement.Succeeded && declined.Change.HasValue &&
                       declined.DeclineReason == reason && declined.PreparedClientRegistration == Guid.Empty &&
                       server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                       first.Session.CaptureNStudyBinding(a1.Contract) == a1.BindingId &&
                       NStudyCount(newType, "Created") == 0 && await b.TouchAsync(1, ct) == 101,
                    "the real peer receives the specific readiness refusal and the original Session keeps serving B");
                trace.Record("WireDeclineLeavesServerR1AndUnrelatedBAvailable", a1);

                using var supplied = PluginBundle.Load($"n-compatible-after-decline-{transport}-{startingAsset}", loadService: false);
                if (alternate is not null)
                {
                    EnsureReplacementReleased(await first.Client.ReplaceAssemblyAsync(alternate.ContractAssembly,
                        supplied.ContractAssembly, TimeSpan.FromSeconds(5), ct), "app supplies its compatible replacement");
                }
                else
                {
                    Ensure(first.Client.RegisterAssembly(supplied.ContractAssembly).Succeeded,
                        "the application explicitly registers compatible local code");
                }
                var preparedId = GetDynamicModule(first.Client, supplied.ContractAssembly).NStudyRegistration;
                var negotiating = first.Client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, approveLocally: null, ct);
                var replacing = server.StageNStudyReplacementAsync(first.ServerConnection, oldAssembly,
                    newAssembly, a1.Contract, NStudyInitiator.Server, approveLocally: null, TimeSpan.FromSeconds(10), ct);
                var resumed = await negotiating.WaitAsync(ct) ?? throw new InvalidOperationException("Expected the deployed asset to prepare.");
                var replacement = await replacing.WaitAsync(ct);
                EnsureReplacementReleased(replacement.Replacement, "normal retry uses the actually deployed compatible asset");
                var current = GetProxy(first.Client, supplied.ContractType);
                Ensure(resumed.LocalRegistration == preparedId && replacement.PreparedClientRegistration == preparedId &&
                       resumed.Change.Id > declined.Change!.Value.Id &&
                       resumed.Change.Target != declined.Change.Value.Target &&
                       resumed.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                       resumed.Binding.Authority == a1.Authority &&
                       await InvokeValueTaskAsync<int>(current, supplied.ContractType, "UnaryAsync", 5, ct) == 6 &&
                       NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 1 &&
                       ReferenceEquals(first.Session, NStudyClientSession(first.Client)) && await b.TouchAsync(2, ct) == 102,
                    "only actual application preparation allows exact R2 business on the original Session");
                trace.Record("ExplicitCompatibleDeploymentAllowsNewExactActivation", a1, resumed.Binding);
            });

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyManualCohortResumesEachActualDataSessionAfterItsOwnReady(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, secondOrNull) = fixture;
                var second = secondOrNull ?? throw new InvalidOperationException("Expected two physical Sessions.");
                var firstA = GetProxy(first.Client, contracts.ContractType);
                var secondA = GetProxy(second.Client, contracts.ContractType);
                var firstB = first.Client.Get<IShutdownCleanupProbe>();
                var secondB = second.Client.Get<IShutdownCleanupProbe>();
                var firstR1 = InvokeValueTaskAsync<int>(firstA, contracts.ContractType, "BlockAsync", ct).AsTask();
                var secondR1 = InvokeValueTaskAsync<int>(secondA, contracts.ContractType, "BlockAsync", ct).AsTask();
                var original = GetDynamicModule(server, oldAssembly);
                await WaitForNStudyAsync(() => original.RemainingCalls == 2, ct);
                Ensure(first.A.Authority.PhysicalSession != second.A.Authority.PhysicalSession &&
                       first.A.Authority.RegistryEpoch == second.A.Authority.RegistryEpoch &&
                       first.A.Target == second.A.Target && first.A.BindingId == second.A.BindingId,
                    "ordinary equal slot numbers belong to two distinct actual Session authorities");
                trace.Record("BothSessionsOwnIndependentR1Uses", first.A, second.A);

                var leadingDecision = new NStudyManualDecision();
                var laterDecision = new NStudyManualDecision();
                try
                {
                    var leading = first.Client.NegotiateNStudyReplacementAsync(
                        NStudyInitiator.Client, leadingDecision.DecideAsync, ct);
                    var following = second.Client.NegotiateNStudyReplacementAsync(
                        NStudyInitiator.Server, laterDecision.DecideAsync, ct);
                    var replacing = server.StageNStudyReplacementOnSessionsAsync(
                        [
                            new(first.ServerConnection, NStudyInitiator.Client, static (_, _) => ValueTask.FromResult(true)),
                            new(second.ServerConnection, NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true))
                        ],
                        oldAssembly, newAssembly, first.A.Contract, TimeSpan.FromSeconds(15), ct);

                    var leadingTicket = await leadingDecision.Seen.WaitAsync(ct);
                    var followingTicket = await laterDecision.Seen.WaitAsync(ct);
                    Ensure(leadingTicket.Authority == first.A.Authority && followingTicket.Authority == second.A.Authority &&
                           leadingTicket.Target == followingTicket.Target && leadingTicket.Original == first.A.Target &&
                           followingTicket.Original == second.A.Target && await firstB.TouchAsync(1, ct) == 101 &&
                           await secondB.TouchAsync(1, ct) == 101, "one staged registration has distinct actual Session receipts");
                    trace.Record("BothPreparedPeersSeeTheirOwnExactTicket", first.A, second.A);

                    leadingDecision.Approve();
                    var r2First = await leading.WaitAsync(ct) ?? throw new InvalidOperationException("Lead Session failed to resume.");
                    await WaitForNStudyAsync(() => second.Session.NStudyPreparedCount == 1, ct);
                    Ensure(r2First.Binding.Authority == first.A.Authority &&
                           r2First.Binding.Target == server.ReadNStudyTarget(newAssembly, first.A.Contract) &&
                           !following.IsCompleted && !replacing.IsCompleted &&
                           !second.ServerConnection.Session.MayBindNStudyTarget(r2First.Binding.Target) &&
                           second.ServerConnection.NStudyBindings.Snapshot() == (1, 1, 1) &&
                           second.Session.CaptureNStudyBinding(second.B.Contract) == second.B.BindingId &&
                           await InvokeValueTaskAsync<int>(firstA, contracts.ContractType, "UnaryAsync", 8, ct) == 9 &&
                           await secondB.TouchAsync(2, ct) == 102 &&
                           !firstR1.IsCompleted && !secondR1.IsCompleted,
                        "lead R2 becomes callable; lagging Session has only B/new-data gate closed and its admitted R1");
                    trace.Record("LeadSessionUsesR2WhileFollowerStillWaitsWithB", r2First.Binding, second.A);

                    laterDecision.Approve();
                    var r2Second = await following.WaitAsync(ct) ?? throw new InvalidOperationException("Follower Session failed to resume.");
                    Ensure(r2Second.Binding.Target == r2First.Binding.Target &&
                           r2Second.Binding.Authority == second.A.Authority &&
                           r2Second.Binding.Authority != r2First.Binding.Authority &&
                           r2First.Binding.BindingId == r2Second.Binding.BindingId &&
                           await InvokeValueTaskAsync<int>(secondA, contracts.ContractType, "UnaryAsync", 10, ct) == 11 &&
                           await firstB.TouchAsync(3, ct) == 103 && !firstR1.IsCompleted && !secondR1.IsCompleted,
                        "follower requires its own real Ready/Activate/Bind; equal token values do not share ownership");
                    trace.Record("FollowerResumesR2OnlyWithItsOwnReadyAndBind", r2First.Binding, r2Second.Binding);

                    oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
                    Ensure(await firstR1.WaitAsync(ct) == 42 && await secondR1.WaitAsync(ct) == 42,
                        "original active calls both finish in R1");
                    var result = await replacing.WaitAsync(ct);
                    EnsureReplacementReleased(result.Replacement, "one coordinated group retired");
                    await WaitForNStudyAsync(() => first.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 0) &&
                        second.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 0), ct);
                    Ensure(result.Peers.Count == 2 && result.Peers[0].Change == leadingTicket &&
                           result.Peers[1].Change == followingTicket && result.Peers.All(peer => peer.ActivationSent && !peer.Terminated) &&
                           NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 1 &&
                           ReferenceEquals(first.Session, NStudyClientSession(first.Client)) &&
                           ReferenceEquals(second.Session, NStudyClientSession(second.Client)),
                        "one physical group retires, original two Sessions keep their own live bindings");
                    trace.Record("BothOldUsesExitAndOriginalSessionsStayConnected", r2First.Binding, r2Second.Binding);
                }
                finally
                {
                    leadingDecision.Approve();
                    laterDecision.Approve();
                }
            });

    private static async Task WithNStudyCoordinationPeersAsync(string transport,
        NStudyCoordinationMode mode, bool twoPeers, Func<NStudyCoordinationFixture, NStudyTrace, CancellationToken, Task> run)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-peers-{transport}-{mode}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "real generated manifests load");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-peers-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client1 = NewNStudyClient(source, port, memoryName);
        await using var client2 = twoPeers ? NewNStudyClient(source, port, memoryName) : null;
        server.NStudyVersionCoordinationMode = client1.NStudyVersionCoordinationMode = mode;
        if (client2 is not null)
            client2.NStudyVersionCoordinationMode = mode;
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
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded, "actual original group registered");
            await server.StartAsync(ct);
            var first = await ConnectPeerAsync(client1);
            var second = client2 is null ? null : await ConnectPeerAsync(client2);
            await run(new(server, contracts, oldAssembly, newAssembly, oldType, newType, first, second), trace, ct);

            async Task<NStudyTestPeer> ConnectPeerAsync(SharpLinkClient client)
            {
                Ensure(client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "compatible local contract registered");
                await client.ConnectAsync(ct);
                var b = await client.PrepareNStudyContractAsync(bCid, staticManifest.RpcAssemblyHash, default, ct);
                var a = await client.PrepareNStudyContractAsync(aCid, manifest.RpcAssemblyHash,
                    server.ReadNStudyTarget(oldAssembly, aCid), ct);
                var session = NStudyClientSession(client);
                var registry = (ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                var peer = registry.SnapshotActive().Single(item => item.NStudyPhysicalIdentity == a.Authority.PhysicalSession);
                Ensure(session.SupportsNStudyCoordination && peer.Session.SupportsNStudyCoordination &&
                       peer.AuthenticationContext?.Subject == "n-validation" &&
                       (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                    "real authenticated transport on that particular physical Session");
                return new(client, session, peer, a, b);
            }
        }
        finally
        {
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private sealed record NStudyTestPeer(SharpLinkClient Client, RpcSession Session,
        ServerConnectionState ServerConnection, NStudyAck A, NStudyAck B);
    private sealed record NStudyCoordinationFixture(SharpLinkServer Server, PluginBundle Contracts,
        Assembly OldAssembly, Assembly NewAssembly, Type OldType, Type NewType,
        NStudyTestPeer First, NStudyTestPeer? Second);
}
