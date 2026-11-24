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
                var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
                var a1 = first.A;
                var a = first.Receiver.Service;
                var b = first.Sibling;
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

                var beforeDecline = first.Session.NStudyBindOperationForDiagnostics;
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
                       first.Session.NStudyBindOperationForDiagnostics == beforeDecline &&
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
                var current = CreateNStudyExactGenerated(first.Client, supplied.ContractType).Service;
                var negotiating = first.Client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, approveLocally: null, ct);
                var replacing = server.StageNStudyReplacementAsync(first.ServerConnection, oldAssembly,
                    newAssembly, a1.Contract, NStudyInitiator.Server, approveLocally: null, TimeSpan.FromSeconds(10), ct);
                var resumed = await negotiating.WaitAsync(ct) ?? throw new InvalidOperationException("Expected the deployed asset to prepare.");
                var replacement = await replacing.WaitAsync(ct);
                EnsureReplacementReleased(replacement.Replacement, "normal retry uses the actually deployed compatible asset");
                Ensure(resumed.LocalRegistration == preparedId && replacement.PreparedClientRegistration == preparedId &&
                       resumed.Change.Id > declined.Change!.Value.Id &&
                       resumed.Change.Target != declined.Change.Value.Target &&
                       resumed.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                       resumed.Binding.Authority == a1.Authority &&
                       await InvokeValueTaskAsync<int>(current, supplied.ContractType, "UnaryAsync", 5, ct) == 6 &&
                       NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 1 &&
                       first.Session.NStudyBindOperationForDiagnostics == beforeDecline + 1 &&
                       first.Session.CaptureNStudyBinding(first.B.Contract) == first.B.BindingId &&
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
                var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, secondOrNull, _) = fixture;
                var second = secondOrNull ?? throw new InvalidOperationException("Expected two physical Sessions.");
                var firstA = first.Receiver.Service;
                var secondA = second.Receiver.Service;
                var firstB = first.Sibling;
                var secondB = second.Sibling;
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

                    var priorLead = first.Session.NStudyBindOperationForDiagnostics;
                    var priorFollower = second.Session.NStudyBindOperationForDiagnostics;
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
                           !firstR1.IsCompleted && !secondR1.IsCompleted &&
                           first.Session.NStudyBindOperationForDiagnostics == priorLead + 1 &&
                           second.Session.NStudyBindOperationForDiagnostics == priorFollower,
                        "lead R2 becomes callable; lagging Session has only B/new-data gate closed and its admitted R1");
                    trace.Record("LeadSessionUsesR2WhileFollowerStillWaitsWithB", r2First.Binding, second.A);

                    laterDecision.Approve();
                    var r2Second = await following.WaitAsync(ct) ?? throw new InvalidOperationException("Follower Session failed to resume.");
                    Ensure(r2Second.Binding.Target == r2First.Binding.Target &&
                           r2Second.Binding.Authority == second.A.Authority &&
                           r2Second.Binding.Authority != r2First.Binding.Authority &&
                           r2First.Binding.BindingId == r2Second.Binding.BindingId &&
                           await InvokeValueTaskAsync<int>(secondA, contracts.ContractType, "UnaryAsync", 10, ct) == 11 &&
                           await firstB.TouchAsync(3, ct) == 103 && !firstR1.IsCompleted && !secondR1.IsCompleted &&
                           first.Session.NStudyBindOperationForDiagnostics == priorLead + 1 &&
                           second.Session.NStudyBindOperationForDiagnostics == priorFollower + 1,
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

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyDeclinedFollowerRecoversOnAFreshPhysicalSession(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, oldType, _, first, secondOrNull, createClient) = fixture;
                var second = secondOrNull ?? throw new InvalidOperationException("Expected a second Session.");
                var localChoice = new NStudyManualDecision();
                try
                {
                    var firstA = first.Receiver.Service;
                    var secondA = second.Receiver.Service;
                    var secondB = second.Sibling;
                    var original = InvokeValueTaskAsync<int>(secondA, contracts.ContractType, "BlockAsync", ct).AsTask();
                    await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
                    var lead = first.Client.NegotiateNStudyReplacementAsync(
                        NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true), ct);
                    var lag = second.Client.NegotiateNStudyReplacementAsync(
                        NStudyInitiator.Server, localChoice.DecideAsync, ct);
                    var change = server.StageNStudyReplacementOnSessionsAsync(
                        [
                            new(first.ServerConnection, NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true)),
                            new(second.ServerConnection, NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true))
                        ],
                        oldAssembly, newAssembly, first.A.Contract, TimeSpan.FromSeconds(15), ct);
                    _ = await localChoice.Seen.WaitAsync(ct);
                    var leadR2 = await lead.WaitAsync(ct) ?? throw new InvalidOperationException("Expected the first peer to activate.");
                    await WaitForNStudyAsync(() => second.Session.NStudyPreparedCount == 1, ct);
                    Ensure(!lag.IsCompleted && !original.IsCompleted &&
                           await InvokeValueTaskAsync<int>(firstA, contracts.ContractType, "UnaryAsync", 8, ct) == 9,
                        "lead is in the committed R2 while the other physical peer has not decided");
                    trace.Record("LeadActivatesBeforeTheLaggingPeerDecides", leadR2.Binding, second.A);

                    localChoice.Decline();
                    Ensure(await lag.WaitAsync(ct) is null && await secondB.TouchAsync(3, ct) == 103,
                        "ordinary refusal is not a disconnect and leaves B callable");
                    oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
                    Ensure(await original.WaitAsync(ct) == 42, "the old already-started call finishes exactly where it started");
                    var outcome = await change.WaitAsync(ct);
                    EnsureReplacementReleased(outcome.Replacement, "the published replacement survives a peer's ordinary decline");
                    Ensure(outcome.Peers[0].ActivationSent && !outcome.Peers[1].ActivationSent &&
                           outcome.Peers[1].DeclineReason == NStudyDeclineReason.Policy &&
                           !outcome.Peers[1].Terminated && NStudyCount(oldType, "Disposed") == 1 &&
                           !second.ServerConnection.Session.MayBindNStudyTarget(leadR2.Binding.Target) &&
                           second.Session.IsConnected, "decline preserves the exact target block after control completes");
                    trace.Record("FollowerDeclineKeepsItsOriginalSessionAndBButNotR2", leadR2.Binding, second.B);

                    try
                    {
                        await second.Receiver.PrepareExact(new(second.A.Authority, second.A.Contract, second.A.Hash,
                            leadR2.Binding.Target), TimeSpan.FromSeconds(5), ct);
                        throw new InvalidOperationException("A normal Bind cannot approve the declined target.");
                    }
                    catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
                    Ensure(second.Session.NStudyPreparedCount == 1 && await secondB.TouchAsync(4, ct) == 104 &&
                           await InvokeValueTaskAsync<int>(firstA, contracts.ContractType, "UnaryAsync", 11, ct) == 12,
                        "the old physical Session neither resumes A nor interrupts either peer's B/R2 use");
                    trace.Record("DeclinedPeerCannotBindR2OnItsOldPhysicalSession", leadR2.Binding, second.B);

                    await second.Client.DisposeAsync();
                    await second.ServerConnection.SessionTask.WaitAsync(ct);
                    await using var fresh = createClient();
                    Ensure(fresh.RegisterAssembly(contracts.ContractAssembly).Succeeded, "the application supplies its compatible local contract");
                    await fresh.ConnectAsync(ct);
                    var freshSession = NStudyClientSession(fresh);
                    Ensure(!ReferenceEquals(freshSession, second.Session) && freshSession.NStudyPreparedCount == 0,
                        "the fresh physical peer does not inherit the old data Session's prepared routes");
                    var (freshB, b) = await PrepareNStudySiblingAsync(fresh, second.B.Contract, ct);
                    var freshReceiver = CreateNStudyExactGenerated(fresh, contracts.ContractType);
                    var a = await freshReceiver.PrepareExact(new(b.Authority, second.A.Contract, second.A.Hash,
                        server.ReadNStudyTarget(newAssembly, second.A.Contract)), TimeSpan.FromSeconds(5), ct);
                    Ensure(a.Authority.PhysicalSession != second.A.Authority.PhysicalSession &&
                           a.Authority.RegistryEpoch == second.A.Authority.RegistryEpoch &&
                           a.Authority == b.Authority && a.Target == leadR2.Binding.Target &&
                           await InvokeValueTaskAsync<int>(freshReceiver.Service, contracts.ContractType,
                               "UnaryAsync", 15, ct) == 16 &&
                           await freshB.TouchAsync(5, ct) == 105 &&
                           ReferenceEquals(first.Session, NStudyClientSession(first.Client)),
                        "ordinary explicit reconnect and fresh binds recover only that peer, without changing the lead Session");
                    trace.Record("FreshDataSessionBindsCurrentR2WithoutChangingLead", leadR2.Binding, a);
                }
                finally { localChoice.Approve(); }
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
        await using var client1 = CreateClient();
        await using var client2 = twoPeers ? CreateClient() : null;
        server.NStudyVersionCoordinationMode = mode;
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
            await run(new(server, contracts, oldAssembly, newAssembly, oldType, newType, first, second, CreateClient), trace, ct);

            async Task<NStudyTestPeer> ConnectPeerAsync(SharpLinkClient client)
            {
                Ensure(client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "compatible local contract registered");
                await client.ConnectAsync(ct);
                var (sibling, b) = await PrepareNStudySiblingAsync(client, bCid, ct);
                var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
                var a = await receiver.PrepareExact(new(b.Authority, aCid, manifest.RpcAssemblyHash,
                    server.ReadNStudyTarget(oldAssembly, aCid)), TimeSpan.FromSeconds(5), ct);
                var session = NStudyClientSession(client);
                var registry = (ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                var peer = registry.SnapshotActive().Single(item => item.NStudyPhysicalIdentity == a.Authority.PhysicalSession);
                Ensure(session.SupportsNStudyCoordination && peer.Session.SupportsNStudyCoordination &&
                       a.Authority == b.Authority && b.Hash == staticManifest.RpcAssemblyHash &&
                       peer.AuthenticationContext?.Subject == "n-validation" &&
                       (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                    "real authenticated transport on that particular physical Session");
                return new(client, session, peer, a, b, receiver, sibling);
            }
        }
        finally
        {
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }

        SharpLinkClient CreateClient()
        {
            var client = NewNStudyClient(source, port, memoryName);
            client.NStudyVersionCoordinationMode = mode;
            return client;
        }
    }

    private sealed record NStudyTestPeer(SharpLinkClient Client, RpcSession Session,
        ServerConnectionState ServerConnection, NStudyAck A, NStudyAck B,
        NStudyExactGenerated Receiver, IShutdownCleanupProbe Sibling);
    private sealed record NStudyCoordinationFixture(SharpLinkServer Server, PluginBundle Contracts,
        Assembly OldAssembly, Assembly NewAssembly, Type OldType, Type NewType,
        NStudyTestPeer First, NStudyTestPeer? Second, Func<SharpLinkClient> CreateClient);
}
