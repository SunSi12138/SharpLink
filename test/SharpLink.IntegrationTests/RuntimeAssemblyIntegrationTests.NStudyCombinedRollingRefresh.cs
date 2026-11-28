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
    public async Task NStudyCombinedRetirementContinuesOnFreshPreparedSession(string transport)
    {
        const int rounds = 3;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = guard.Token;
        var bound = TimeSpan.FromSeconds(10);
        using var contract = PluginBundle.Load("combined-fixed-server-contract", loadService: false);
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var appManifest).Succeeded &&
               SharpLinkAssemblyManifestLoader.TryLoad(contract.ContractAssembly, out _).Succeeded, "generated source loaded");
        var aId = CombinedContractId(contract);
        var bId = appManifest!.Contracts.Single(x => x.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var source = new FixedGeneratedManifestSource([appManifest]);
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        var shm = transport == "tcp" ? null : $"sl-combined-{Guid.NewGuid():N}";
        if (shm is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(shm);
        var port = shm is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, shm);
        server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        var sl = new CombinedServiceLoad(contract.ContractAssembly);
        var cl = new CombinedClientLoad(0);
        var clientReleased = new List<WeakReference>();
        var serverReleased = new List<WeakReference>();
        try
        {
            Ensure(server.RegisterAssembly(contract.ContractAssembly).Succeeded && server.RegisterAssembly(sl.Assembly).Succeeded &&
                   client.RegisterAssembly(cl.Assembly).Succeeded && !ReferenceEquals(contract.ContractAssembly, cl.Assembly),
                "real independent client and server contracts and original service register");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var b = client.CreateProxy<IShutdownCleanupProbe>();
            await b.PrepareAsync(bound, ct);
            cl.Capture(client);
            await cl.Proxy.Prepare(bound, ct);
            Ensure(await cl.Call("UnaryAsync", 0, ct) == 1 && await b.Service.TouchAsync(0, ct) == 100,
                "initial generated A and independent B both serve");
            var conn = NStudyClientConnection(client);
            var session = conn.Session;
            var sc = NStudyServerConnection(server);
            var authority = server.GetVersionPeers().Single();
            Ensure(sc.AuthenticationContext?.Subject == "n-validation" && session.SupportsNStudyCoordination &&
                   authority.PhysicalSessionId == sc.NStudyPhysicalIdentity &&
                   (shm is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "real authenticated original physical Session and transport");
            var participant = new SharpLinkVersionParticipant(authority, SharpLinkVersionInitiator.Server);
            var serverReg = server.GetLocalVersionInventory().Registrations.Single(x => x.ContractId == aId);
            var clientReg = client.GetLocalVersionInventory().Registrations.Single(x => x.ContractId == aId);
            var bToken = session.CaptureNStudyBinding(bId);
            var aToken = session.CaptureNStudyBinding(aId);
            var bind = session.NStudyBindOperationForDiagnostics;
            var (serverRev, localRev) = (server.GetLocalVersionInventory().Revision, client.GetLocalVersionInventory().Revision);
            await CombinedCountersAsync("start", client, conn, sc, 2, 0, ct);
            CombinedEvent(new
            {
                edge = "Start",
                transport,
                session = authority.PhysicalSessionId,
                serverInstance = authority.ServerInstanceId,
                registryEpoch = authority.ServerRegistryEpoch,
                binding = aToken,
                bToken,
                bind,
                frameLimit = session.NegotiatedMaxFramePayloadBytes,
                server = serverReg.RegistrationId,
                local = clientReg.RegistrationId,
                serverRev,
                localRev
            });
            int[] frameLimits = [128 * 1024, 192 * 1024, 256 * 1024];
            for (int step = 1; step <= rounds; step++)
            {
                var nextService = new CombinedServiceLoad(contract.ContractAssembly);
                var nextClient = new CombinedClientLoad(step);
                try
                {
                    var result = await CombinedStepAsync(server, client, conn, sc, b, sl, cl, nextService, nextClient,
                        participant, aId, bId, aToken, bToken, bind, serverReg, clientReg, bound, frameLimits[step - 1], step, ct);
                    var oldServer = serverReg.RegistrationId;
                    var oldLocal = clientReg.RegistrationId;
                    var oldSession = authority.PhysicalSessionId;
                    conn = result.Link.Connection;
                    session = conn.Session;
                    sc = result.Link.ServerConnection;
                    authority = result.Link.Authority;
                    participant = new SharpLinkVersionParticipant(authority, SharpLinkVersionInitiator.Server);
                    serverReg = result.Change.Candidate;
                    clientReg = result.Local;
                    aToken = session.CaptureNStudyBinding(aId);
                    bToken = session.CaptureNStudyBinding(bId);
                    bind = session.NStudyBindOperationForDiagnostics;
                    serverReleased.Add(sl.Retire());
                    clientReleased.Add(cl.Retire());
                    sl = nextService;
                    cl = nextClient;
                    await RequireCombinedCollectionAsync(serverReleased, clientReleased, ct);
                    CombinedEvent(new
                    {
                        edge = "Settled",
                        step,
                        originalSession = oldSession,
                        session = authority.PhysicalSessionId,
                        fromServer = oldServer,
                        toServer = serverReg.RegistrationId,
                        fromLocal = oldLocal,
                        toLocal = clientReg.RegistrationId,
                        binding = aToken,
                        bToken,
                        bind,
                        counts = result.Settled,
                        serverReleased = serverReleased.Count,
                        clientReleased = clientReleased.Count
                    });
                }
                catch
                {
                    nextService.Dispose();
                    nextClient.Dispose();
                    throw;
                }
            }

            EnsureUnregisterReleased(await server.UnregisterAssemblyAsync(sl.Assembly, bound, ct), "last server service");
            Ensure(NStudyCount(sl.ServiceType, "Disposed") == 1, "last server handler disposed");
            EnsureUnregisterReleased(await client.UnregisterAssemblyAsync(cl.Assembly, bound, ct), "last client contract");
            serverReleased.Add(sl.Retire());
            clientReleased.Add(cl.Retire());
            await RequireCombinedCollectionAsync(serverReleased, clientReleased, ct);
            var last = await CombinedCountersAsync("after unregister", client, conn, sc, 1, 0, ct);
            Ensure(session.NStudyBindOperationForDiagnostics == bind && session.CaptureNStudyBinding(bId) == bToken &&
                   await b.Service.TouchAsync(990, ct) == 1090 && server.GetVersionPeers().Single() == authority,
                "unregister clears A while original B and the same Session live");
            CombinedEvent(new
            {
                edge = "LastRetired",
                counts = last,
                bind,
                bToken,
                serverReleased = serverReleased.Count,
                clientReleased = clientReleased.Count,
                session = authority.PhysicalSessionId
            });
            await client.StopAsync(ct);
            await server.StopAsync(bound, ct);
            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 && client.ActiveClientStreamCount == 0 &&
                   ServerCallAdmissionDiagnostics.ActiveCallCount(server) == 0 && client.ReadyConnectionCount == 0 &&
                   server.GetVersionPeers().Count == 0, "normal final stop leaves no pending/active work or Ready endpoint");
            CombinedEvent(new
            {
                edge = "Stopped",
                rounds,
                clientPending = client.PendingCallCount,
                clientCalls = client.ActiveClientCallCount,
                clientStreams = client.ActiveClientStreamCount,
                serverCalls = ServerCallAdmissionDiagnostics.ActiveCallCount(server),
                peers = server.GetVersionPeers().Count
            });
        }
        finally
        {
            cl.Dispose();
            sl.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CombinedContractId(PluginBundle contract)
    {
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(contract.ContractAssembly, out var manifest).Succeeded,
            "real generated contract descriptor");
        return manifest!.Contracts.Single(c => c.ContractType == contract.ContractType).ContractId;
    }

    private static async Task<(SharpLinkVersionChange Change, SharpLinkVersionRegistration Local, RefreshedLink Link, CombinedCounters Settled)>
        CombinedStepAsync(SharpLinkServer server, SharpLinkClient client, ClientConnection conn, ServerConnectionState sc,
        RpcProxy<IShutdownCleanupProbe> b, CombinedServiceLoad oldServer, CombinedClientLoad oldClient,
        CombinedServiceLoad nextServer, CombinedClientLoad nextClient, SharpLinkVersionParticipant peer, long aId, long bId,
        long originalAToken, long bToken, ulong priorBind, SharpLinkVersionRegistration remote, SharpLinkVersionRegistration local,
        TimeSpan bound, int frameLimit, int step, CancellationToken ct)
    {
        var active = oldClient.Call("BlockAsync", ct).AsTask();
        Task<SharpLinkAssemblyReplacementResult> clientDrain;
        Task<SharpLinkServerVersionChangeResult> serverDrain;
        SharpLinkClientVersionChangeResult resumed;
        SharpLinkVersionRegistration targetClient;
        long token;
        RefreshedLink refreshed;
        try
        {
            await ReadNStudyTask(oldServer.ServiceType, "BlockStarted").WaitAsync(ct);
            clientDrain = client.ReplaceAssemblyAsync(oldClient.Assembly, nextClient.Assembly, bound, ct).AsTask();
            nextClient.Capture(client);
            targetClient = client.GetLocalVersionInventory().Registrations.Single(x => x.ContractId == aId);
            Ensure(!active.IsCompleted && !clientDrain.IsCompleted && targetClient.RegistrationId != local.RegistrationId &&
                   targetClient.AssemblyHash == local.AssemblyHash && targetClient.PublicationGeneration > local.PublicationGeneration,
                $"round {step}: new client registration is genuinely published while the original remains active");
            var wait = client.JoinVersionChangeAsync(nextClient.Proxy.Local, peer, cancellationToken: ct).AsTask();
            serverDrain = server.ReplaceAssemblyCoordinatedAsync(oldServer.Assembly, nextServer.Assembly, aId, [peer],
                bound, cancellationToken: ct).AsTask();
            resumed = await wait.WaitAsync(ct);
            var change = resumed.Change;
            Ensure(resumed.Resumed && resumed.DeclineReason == SharpLinkVersionDeclineReason.None &&
                   resumed.LocalRegistrationId == targetClient.RegistrationId && change.Current == remote &&
                   change.Candidate.RegistrationId != remote.RegistrationId &&
                   change.Candidate.PublicationGeneration > remote.PublicationGeneration &&
                   change.Candidate.ContractId == aId && change.Candidate.AssemblyHash == remote.AssemblyHash &&
                   change.Session == peer.Session, $"round {step}: paired Resume exact: {resumed.DeclineReason}");
            token = conn.Session.CaptureNStudyBinding(aId);
            Ensure(await nextClient.Call("UnaryAsync", step, ct) == step + 1 && NStudyCount(nextServer.ServiceType, "Created") == 1 &&
                   await b.Service.TouchAsync(step, ct) == step + 100 &&
                   conn.Session.NStudyBindOperationForDiagnostics == priorBind + 1 &&
                   conn.Session.CaptureNStudyBinding(bId) == bToken, $"round {step}: new A and original B on exact paths");
            var served = await client.QueryPeerVersionInventoryAsync(ct);
            var reverse = await server.QueryPeerVersionInventoryAsync(peer.Session, ct);
            Ensure(served.Session == peer.Session && reverse.Session == peer.Session &&
                   served.Registrations.Contains(change.Candidate) && reverse.Registrations.Contains(targetClient) &&
                   ReferenceEquals(conn, NStudyClientConnection(client)) && ReferenceEquals(sc, NStudyServerConnection(server)),
                $"round {step}: both publishers and physical identities exact");
            var held = await CombinedCountersAsync("both owners held", client, conn, sc, 2, 1, ct);
            Ensure(!active.IsCompleted && !serverDrain.IsCompleted && !clientDrain.IsCompleted && NStudyCount(oldServer.ServiceType, "Disposed") == 0,
                $"round {step}: both original physical drains still wait");
            CombinedEvent(new
            {
                edge = "Held",
                step,
                session = peer.Session.PhysicalSessionId,
                binding = token,
                originalAToken,
                bToken,
                bind = conn.Session.NStudyBindOperationForDiagnostics,
                server = change.Candidate.RegistrationId,
                local = targetClient.RegistrationId,
                counts = held
            });
            refreshed = await RefreshWhileOriginalOwnersAreLiveAsync(server, client, conn, sc, b, nextClient, peer.Session,
                change.Candidate, targetClient, aId, bId, originalAToken, bToken, frameLimit, step, bound, ct);
            Ensure(!active.IsCompleted && !serverDrain.IsCompleted && !clientDrain.IsCompleted &&
                   NStudyCount(oldServer.ServiceType, "Disposed") == 0,
                $"round {step}: both original registry drains survive actual refresh");
        }
        finally
        {
            oldServer.ServiceType.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }

        Ensure(await active.WaitAsync(ct) == 42, $"round {step}: admitted old call completes on original");
        EnsureReplacementReleased(await clientDrain.WaitAsync(ct), "combined local drain");
        var serverDone = await serverDrain.WaitAsync(ct);
        EnsureReplacementReleased(serverDone.Replacement, "combined remote drain");
        var sent = serverDone.Peers.Single();
        Ensure(sent.Session == peer.Session && sent.Change == resumed.Change && sent.ActivationSent &&
               sent.PreparedClientRegistrationId == targetClient.RegistrationId && NStudyCount(oldServer.ServiceType, "Disposed") == 1,
            $"round {step}: server ActivationSent matches actual Resume and old handler disposed once");
        await WaitForNStudyAsync(() => !conn.Session.IsConnected &&
            NStudyServerRegistry(server).SnapshotActive().All(p => !ReferenceEquals(p, sc)) &&
            sc.NStudyBindings.Snapshot() == (0, 0, 0) &&
            conn.PendingCalls.ActiveCount == 0 && conn.PendingCalls.NStudyProducerCount == 0 &&
            conn.Session.StreamManager.ActiveStreamCount == 0 && sc.Session.StreamManager.ActiveStreamCount == 0, ct);
        Ensure(ReferenceEquals(NStudyClientConnection(client), refreshed.Connection) &&
               server.GetVersionPeers().Single() == refreshed.Authority, $"round {step}: source session physically retired");
        var settled = await CombinedCountersAsync("after original drained", client,
            refreshed.Connection, refreshed.ServerConnection, 2, 0, ct);
        CombinedEvent(new
        {
            edge = "OriginalRetired",
            step,
            from = peer.Session.PhysicalSessionId,
            current = refreshed.Authority.PhysicalSessionId,
            result = 42,
            disposed = 1,
            oldConnected = conn.Session.IsConnected,
            oldPending = conn.PendingCalls.ActiveCount,
            oldBindings = sc.NStudyBindings.Snapshot().Item1
        });
        return (resumed.Change, targetClient, refreshed, settled);
    }

    private sealed record RefreshedLink(ClientConnection Connection, ServerConnectionState ServerConnection, SharpLinkVersionSession Authority);

    private static async Task<RefreshedLink> RefreshWhileOriginalOwnersAreLiveAsync(
        SharpLinkServer server, SharpLinkClient client, ClientConnection original, ServerConnectionState oldPeer,
        RpcProxy<IShutdownCleanupProbe> b, CombinedClientLoad successor, SharpLinkVersionSession prior,
        SharpLinkVersionRegistration serverReg, SharpLinkVersionRegistration localReg, long aId, long bId,
        long previousAToken, long previousBToken, int frameBytes, int step, TimeSpan bound, CancellationToken ct)
    {
        var serverBefore = server.GetLocalVersionInventory();
        var clientBefore = client.GetLocalVersionInventory();
        Ensure(serverBefore.Registrations.Contains(serverReg) && clientBefore.Registrations.Contains(localReg) &&
               frameBytes != original.Session.NegotiatedMaxFramePayloadBytes &&
               (original.Session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0,
            $"round {step}: current publications and real rolling capability");
        var desired = await server.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = frameBytes },
            SharpLinkSessionRolloutMode.RollingRefresh, ct);
        await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 &&
            !ReferenceEquals(original, NStudyClientConnection(client)), ct);
        var current = NStudyClientConnection(client);
        var queried = await client.QueryPeerVersionInventoryAsync(ct);
        var authority = queried.Session!.Value;
        var newPeer = NStudyServerRegistry(server).SnapshotActive().Single(p => p.NStudyPhysicalIdentity == authority.PhysicalSessionId);
        var reverse = await server.QueryPeerVersionInventoryAsync(authority, ct);
        Ensure(authority.PhysicalSessionId != prior.PhysicalSessionId && authority.ServerInstanceId == prior.ServerInstanceId &&
               authority.ServerRegistryEpoch == prior.ServerRegistryEpoch &&
               desired.ServerInstanceId == authority.ServerInstanceId && desired.Configuration.MaxFramePayloadBytes == frameBytes &&
               current.Session.NegotiatedMaxFramePayloadBytes == frameBytes &&
               newPeer.AuthenticationContext?.Subject == "n-validation" &&
               queried.PublisherEpoch == serverBefore.PublisherEpoch && queried.Revision == serverBefore.Revision &&
               queried.Registrations.SequenceEqual(serverBefore.Registrations) &&
               reverse.Session == authority && reverse.PublisherEpoch == clientBefore.PublisherEpoch &&
               reverse.Revision == clientBefore.Revision && reverse.Registrations.SequenceEqual(clientBefore.Registrations) &&
               !original.CanAcceptCalls && original.Session.IsConnected && original.PendingCalls.ActiveCount == 1 &&
               !ReferenceEquals(oldPeer, newPeer),
            $"round {step}: authenticated physical authority changed while published versions and original pending call are unchanged");
        var sourceHeads = oldPeer.LastAcceptedRequestId;
        var freshHeads = newPeer.LastAcceptedRequestId;
        await ExpectPrecommitUnavailableAsync(() => successor.Call("UnaryAsync", step, ct));
        await ExpectPrecommitUnavailableAsync(() => b.Service.TouchAsync(step, ct));
        var empty = await CombinedCountersAsync("fresh session before explicit Prepare", client, current, newPeer, 0, 0, ct);
        Ensure(current.Session.NStudyBindOperationForDiagnostics == 0 &&
               sourceHeads == oldPeer.LastAcceptedRequestId && freshHeads == newPeer.LastAcceptedRequestId,
            $"round {step}: inventory and old proxy calls did not prepare or enter business on either link");
        CombinedEvent(new
        {
            edge = "FreshUnprepared",
            step,
            from = prior.PhysicalSessionId,
            session = authority.PhysicalSessionId,
            serverInstance = authority.ServerInstanceId,
            registryEpoch = authority.ServerRegistryEpoch,
            frame = frameBytes,
            configurationGeneration = desired.Generation,
            server = serverReg.RegistrationId,
            local = localReg.RegistrationId,
            serverRev = queried.Revision,
            localRev = reverse.Revision,
            sourcePending = original.PendingCalls.ActiveCount,
            freshBind = 0,
            counts = empty
        });

        // The same application handles are explicitly prepared on the new Session.
        await b.PrepareAsync(bound, ct);
        await successor.Proxy.Prepare(bound, ct);
        var nowA = current.Session.CaptureNStudyBinding(aId);
        var nowB = current.Session.CaptureNStudyBinding(bId);
        Ensure(current.Session.NStudyBindOperationForDiagnostics == 2 && nowA == previousAToken && nowB == previousBToken &&
               await successor.Call("UnaryAsync", step + 100, ct) == step + 101 &&
               await b.Service.TouchAsync(step + 100, ct) == step + 200 &&
               newPeer.LastAcceptedRequestId != freshHeads && oldPeer.LastAcceptedRequestId == sourceHeads &&
               original.Session.IsConnected && original.PendingCalls.ActiveCount == 1,
            $"round {step}: repeated numeric tokens gained new authority only from explicit Prepare on fresh physical Session");
        var ready = await CombinedCountersAsync("fresh session after explicit Prepare", client, current, newPeer, 2, 0, ct);
        CombinedEvent(new
        {
            edge = "FreshPrepared",
            step,
            session = authority.PhysicalSessionId,
            binding = nowA,
            bToken = nowB,
            oldBinding = previousAToken,
            oldBToken = previousBToken,
            bind = 2,
            sourcePending = original.PendingCalls.ActiveCount,
            counts = ready
        });
        return new(current, newPeer, authority);
    }

    private static void CombinedEvent<T>(T record) => Console.WriteLine("COMBINED_REFRESH " + JsonSerializer.Serialize(record));
}
