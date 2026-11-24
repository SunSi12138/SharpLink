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
    public async Task NStudyRollingRefreshCancelsUncommittedManualApproval(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = guard.Token;
        var bound = TimeSpan.FromSeconds(5);
        using var contract = PluginBundle.Load("precommit-server-contract", loadService: false);
        using var localLoad = new CombinedClientLoad(0);
        using var sourceService = new CombinedServiceLoad(contract.ContractAssembly);
        using var abandoned = new CombinedServiceLoad(contract.ContractAssembly);
        using var retry = new CombinedServiceLoad(contract.ContractAssembly);
        var staticLoaded = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var actualLoaded = SharpLinkAssemblyManifestLoader.TryLoad(contract.ContractAssembly, out var actualManifest);
        Ensure(staticLoaded.Succeeded && actualLoaded.Succeeded,
            "real current generated contracts");
        var src = new FixedGeneratedManifestSource([staticManifest!]);
        var aId = actualManifest!.Contracts.Single(x => x.ContractType == contract.ContractType).ContractId;
        var bId = staticManifest!.Contracts.Single(x => x.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(src, provider);
        var shm = transport == "tcp" ? null : $"sl-precommit-{Guid.NewGuid():N}";
        if (shm is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(shm);
        var port = shm is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(src, port, shm);
        server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Manual);
        client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Manual);
        Ensure(server.RegisterAssembly(contract.ContractAssembly).Succeeded &&
               server.RegisterAssembly(sourceService.Assembly).Succeeded &&
               client.RegisterAssembly(localLoad.Assembly).Succeeded, "R1 and nominal client assembly really registered");
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var b = client.CreateProxy<IShutdownCleanupProbe>();
        localLoad.Capture(client);
        await b.PrepareAsync(bound, ct);
        await localLoad.Proxy.Prepare(bound, ct);
        Ensure(await localLoad.Call("UnaryAsync", 6, ct) == 7 && await b.Service.TouchAsync(6, ct) == 106,
            "real original A and independent B work");
        var old = NStudyClientConnection(client);
        var oldPeer = NStudyServerConnection(server);
        var original = server.GetVersionPeers().Single();
        var serverOriginal = server.GetLocalVersionInventory();
        var clientOriginal = client.GetLocalVersionInventory();
        var originalA = serverOriginal.Registrations.Single(x => x.ContractId == aId);
        var localA = clientOriginal.Registrations.Single(x => x.ContractId == aId);
        var oldAToken = old.Session.CaptureNStudyBinding(aId);
        var oldBToken = old.Session.CaptureNStudyBinding(bId);
        Ensure(oldPeer.AuthenticationContext?.Subject == "n-validation" && oldPeer.NStudyPhysicalIdentity == original.PhysicalSessionId &&
               old.Session.NStudyHandshakeCoordinationMode == NStudyCoordinationMode.Manual &&
               old.Session.SupportsNStudyOfferAdmission && old.Session.NegotiatedMaxFramePayloadBytes > 128 * 1024 &&
               (shm is null ? old.Session.RemoteEndPoint is IPEndPoint : old.Session.Input is SharedMemoryPipeReader),
            "genuine authenticated physical Manual Session");
        var started = new TaskCompletionSource<(SharpLinkVersionChange Change, long Started)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDecision = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisionExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstServerVotes = 0;
        async ValueTask<SharpLinkVersionDecision> WaitOriginalDecisionAsync(SharpLinkVersionChange change, CancellationToken token)
        {
            started.SetResult((change, Stopwatch.GetTimestamp()));
            try { return await releaseDecision.Task.WaitAsync(token); }
            finally { decisionExited.TrySetResult(token.IsCancellationRequested); }
        }

        var participant = new SharpLinkVersionParticipant(original, SharpLinkVersionInitiator.Server);
        var pendingJoin = client.JoinVersionChangeAsync(localLoad.Proxy.Local, participant, WaitOriginalDecisionAsync, ct).AsTask();
        var pendingReplace = server.ReplaceAssemblyCoordinatedAsync(sourceService.Assembly, abandoned.Assembly, aId,
            [participant], bound, (_, _) => { Interlocked.Increment(ref firstServerVotes); return ValueTask.FromResult(SharpLinkVersionDecision.Approve); },
            ct).AsTask();
        try
        {
            var (proposed, callbackStart) = await started.Task.WaitAsync(ct);
            var initial = await CombinedCountersAsync("before publish or client vote", client, old, oldPeer, 2, 0, ct);
            Ensure(proposed.Session == original && proposed.Current == originalA && proposed.Candidate != originalA &&
                   proposed.Candidate.AssemblyHash == originalA.AssemblyHash && firstServerVotes == 1 &&
                   old.Session.RuntimeContext.NStudyApprovals.ActiveCount == 1 &&
                   !pendingJoin.IsCompleted && !pendingReplace.IsCompleted && !decisionExited.Task.IsCompleted &&
                   NStudyCount(abandoned.ServiceType, "Created") == 0 &&
                   SamePrecommitInventory(serverOriginal, server.GetLocalVersionInventory()) &&
                   SamePrecommitInventory(clientOriginal, client.GetLocalVersionInventory()) &&
                   old.Session.CaptureNStudyBinding(aId) == oldAToken && old.Session.CaptureNStudyBinding(bId) == oldBToken &&
                   await localLoad.Call("UnaryAsync", 7, ct) == 8 && await b.Service.TouchAsync(7, ct) == 107,
                "admitted Manual callback, not committed; original published A and B still usable");
            PrecommitEvent(new { edge = "ApprovalWaiting", transport, old = original.PhysicalSessionId, instance = original.ServerInstanceId,
                epoch = original.ServerRegistryEpoch, current = originalA.RegistrationId, attempted = proposed.Candidate.RegistrationId,
                client = localA.RegistrationId, serverRev = serverOriginal.Revision, clientRev = clientOriginal.Revision,
                approvalActive = old.Session.RuntimeContext.NStudyApprovals.ActiveCount, counts = initial });
            var desired = await server.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = 128 * 1024 },
                SharpLinkSessionRolloutMode.RollingRefresh, ct);
            await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 &&
                !ReferenceEquals(old, NStudyClientConnection(client)) && !old.Session.IsConnected &&
                NStudyServerRegistry(server).SnapshotActive().All(p => !ReferenceEquals(p, oldPeer)), ct);
            var fresh = NStudyClientConnection(client);
            var remote = await client.QueryPeerVersionInventoryAsync(ct);
            var freshSession = remote.Session!.Value;
            var freshPeer = NStudyServerConnection(server);
            var remoteLocal = await server.QueryPeerVersionInventoryAsync(freshSession, ct);
            Ensure(freshSession.PhysicalSessionId != original.PhysicalSessionId && freshSession.ServerInstanceId == original.ServerInstanceId &&
                   freshSession.ServerRegistryEpoch == original.ServerRegistryEpoch && desired.Configuration.MaxFramePayloadBytes == 128 * 1024 &&
                   fresh.Session.NegotiatedMaxFramePayloadBytes == 128 * 1024 && freshPeer.AuthenticationContext?.Subject == "n-validation" &&
                   freshPeer.NStudyPhysicalIdentity == freshSession.PhysicalSessionId &&
                   SamePrecommitInventory(serverOriginal, remote) && SamePrecommitInventory(clientOriginal, remoteLocal) &&
                   SamePrecommitInventory(serverOriginal, server.GetLocalVersionInventory()), "new physical Ready without a registry transition");
            string clientOutcome;
            try
            {
                _ = await pendingJoin.WaitAsync(ct);
                throw new InvalidOperationException("A retired physical Session must not resume.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable &&
                error.InnerException is OperationCanceledException && old.Session.LifetimeToken.IsCancellationRequested &&
                !ct.IsCancellationRequested)
            {
                clientOutcome = error.Code.ToString();
            }
            var stopped = await pendingReplace.WaitAsync(ct);
            var report = stopped.Peers.Single();
            var decisionCancelled = await decisionExited.Task.WaitAsync(ct);
            var decisionElapsed = Stopwatch.GetElapsedTime(callbackStart);
            await WaitForNStudyAsync(() => old.Session.RuntimeContext.NStudyApprovals.ActiveCount == 0 &&
                                           oldPeer.Session.RuntimeContext.NStudyApprovals.ActiveCount == 0, ct);
            Ensure(decisionElapsed < TimeSpan.FromSeconds(3) && !ct.IsCancellationRequested && decisionCancelled && !stopped.Replacement.Succeeded &&
                   stopped.Replacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                   report.DeclineReason == SharpLinkVersionDeclineReason.None && !report.ActivationSent && report.Session == original && report.Change == proposed &&
                   report.PreparedClientRegistrationId is null && !old.Session.NStudyOfferPendingForDiagnostics &&
                   NStudyCount(abandoned.ServiceType, "Created") == 0 &&
                   SamePrecommitInventory(serverOriginal, server.GetLocalVersionInventory()) &&
                   SamePrecommitInventory(clientOriginal, client.GetLocalVersionInventory()),
                "refresh and source retirement cancel the actual callback before its approval timeout, no publication or activation");
            PrecommitEvent(new { edge = "OriginalAborted", old = original.PhysicalSessionId, session = freshSession.PhysicalSessionId,
                clientOutcome, serverError = stopped.Replacement.Error!.Code.ToString(), serverDecline = report.DeclineReason.ToString(),
                activation = report.ActivationSent, cancelled = decisionCancelled, elapsedWithinApproval = true,
                sourceClosed = !old.Session.IsConnected, approvals = old.Session.RuntimeContext.NStudyApprovals.ActiveCount });
            var failedContext = abandoned.Retire();
            await RequireCombinedCollectionAsync([failedContext], [], ct);
            var initialHeads = freshPeer.LastAcceptedRequestId;
            await ExpectPrecommitUnavailableAsync(() => localLoad.Call("UnaryAsync", 8, ct));
            await ExpectPrecommitUnavailableAsync(() => b.Service.TouchAsync(8, ct));
            var empty = await CombinedCountersAsync("fresh before Prepare", client, fresh, freshPeer, 0, 0, ct);
            Ensure(fresh.Session.NStudyBindOperationForDiagnostics == 0 && initialHeads == freshPeer.LastAcceptedRequestId,
                "fresh peer does not get grants from a cancelled attempt or inventory");
            PrecommitEvent(new { edge = "FreshUnprepared", session = freshSession.PhysicalSessionId,
                server = originalA.RegistrationId, local = localA.RegistrationId,
                serverRev = remote.Revision, clientRev = remoteLocal.Revision, failedCollected = !failedContext.IsAlive,
                bind = fresh.Session.NStudyBindOperationForDiagnostics, counts = empty });

            await b.PrepareAsync(bound, ct);
            await localLoad.Proxy.Prepare(bound, ct);
            Ensure(fresh.Session.NStudyBindOperationForDiagnostics == 2 && await localLoad.Call("UnaryAsync", 8, ct) == 9 &&
                   NStudyCount(sourceService.ServiceType, "Created") == 1 && await b.Service.TouchAsync(8, ct) == 108,
                "explicit Prepare reaches R1 and B again, independently of abandoned vote");
            PrecommitEvent(new { edge = "OriginalPreparedAgain", session = freshSession.PhysicalSessionId,
                server = originalA.RegistrationId, bind = fresh.Session.NStudyBindOperationForDiagnostics });

            var retryParticipant = new SharpLinkVersionParticipant(freshSession, SharpLinkVersionInitiator.Server);
            var retryClientVotes = 0;
            var retryServerVotes = 0;
            SharpLinkVersionChange? retrySeen = null;
            var joining = client.JoinVersionChangeAsync(localLoad.Proxy.Local, retryParticipant, (change, _) =>
            {
                Interlocked.Increment(ref retryClientVotes);
                retrySeen = change;
                return ValueTask.FromResult(SharpLinkVersionDecision.Approve);
            }, ct).AsTask();
            var replacing = server.ReplaceAssemblyCoordinatedAsync(sourceService.Assembly, retry.Assembly, aId, [retryParticipant], bound,
                (_, _) => { Interlocked.Increment(ref retryServerVotes); return ValueTask.FromResult(SharpLinkVersionDecision.Approve); }, ct).AsTask();
            var resumed = await joining.WaitAsync(ct);
            var installed = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(installed.Replacement, "new explicit attempt after refresh");
            var sent = installed.Peers.Single();
            Ensure(resumed.Resumed && resumed.LocalRegistrationId == localA.RegistrationId && resumed.Change == retrySeen &&
                   resumed.Change.Session == freshSession && resumed.Change.Current == originalA && resumed.Change.Candidate != proposed.Candidate &&
                   sent.Change == resumed.Change && sent.ActivationSent && sent.PreparedClientRegistrationId == localA.RegistrationId &&
                   retryClientVotes == 1 && retryServerVotes == 1 && NStudyCount(sourceService.ServiceType, "Disposed") == 1 &&
                   await localLoad.Call("UnaryAsync", 9, ct) == 10 && NStudyCount(retry.ServiceType, "Created") == 1 &&
                   await b.Service.TouchAsync(9, ct) == 109 && fresh.Session.NStudyBindOperationForDiagnostics == 3,
                "fresh Manual votes and matching activation/resume, exact new application handler and independent B");
            var current = await client.QueryPeerVersionInventoryAsync(ct);
            Ensure(current.Session == freshSession && current.Registrations.Contains(resumed.Change.Candidate) &&
                   !current.Registrations.Contains(proposed.Candidate),
                "fresh registry contains only real published R2");
            var oldContext = sourceService.Retire();
            await RequireCombinedCollectionAsync([failedContext, oldContext], [], ct);
            var settled = await CombinedCountersAsync("on fresh R2 after retry", client, fresh, freshPeer, 2, 0, ct);
            PrecommitEvent(new { edge = "FreshManualRetry", session = freshSession.PhysicalSessionId,
                original = originalA.RegistrationId, abandoned = proposed.Candidate.RegistrationId,
                current = resumed.Change.Candidate.RegistrationId, clientVote = retryClientVotes, serverVote = retryServerVotes,
                bind = fresh.Session.NStudyBindOperationForDiagnostics, counts = settled, inactiveAlcs = 2 });

            EnsureUnregisterReleased(await client.UnregisterAssemblyAsync(localLoad.Assembly, bound, ct), "final client contract");
            EnsureUnregisterReleased(await server.UnregisterAssemblyAsync(retry.Assembly, bound, ct), "final server service");
            var endClient = localLoad.Retire();
            var endServer = retry.Retire();
            await RequireCombinedCollectionAsync([failedContext, oldContext, endServer], [endClient], ct);
            await CombinedCountersAsync("A unregistered", client, fresh, freshPeer, 1, 0, ct);
            Ensure(await b.Service.TouchAsync(10, ct) == 110, "independent B remains available before stop");
            await client.StopAsync(ct);
            await server.StopAsync(bound, ct);
            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 && client.ActiveClientStreamCount == 0 &&
                   ServerCallAdmissionDiagnostics.ActiveCallCount(server) == 0 && server.GetVersionPeers().Count == 0,
                "final clean Stop");
            PrecommitEvent(new { edge = "Stopped", alcs = 4, pending = client.PendingCallCount, clientCalls = client.ActiveClientCallCount,
                streams = client.ActiveClientStreamCount, serverCalls = ServerCallAdmissionDiagnostics.ActiveCallCount(server),
                peers = server.GetVersionPeers().Count });
        }
        finally { releaseDecision.TrySetResult(SharpLinkVersionDecision.Reject); }
    }

    private static bool SamePrecommitInventory(SharpLinkVersionInventory a, SharpLinkVersionInventory b)
        => a.PublisherEpoch == b.PublisherEpoch && a.Revision == b.Revision && a.Registrations.SequenceEqual(b.Registrations);

    private static async Task ExpectPrecommitUnavailableAsync(Func<ValueTask<int>> call)
    {
        try
        {
            _ = await call();
            throw new InvalidOperationException("A prepared route leaked across a physical refresh");
        }
        catch (SharpLinkException e) when (e.Code == SharpLinkErrorCode.Unavailable) { }
        catch (TargetInvocationException e) when (e.InnerException is SharpLinkException { Code: SharpLinkErrorCode.Unavailable }) { }
    }

    private static void PrecommitEvent<T>(T record) => Console.WriteLine("PRECOMMIT_REFRESH " + JsonSerializer.Serialize(record));

    private sealed record CombinedCounters(int Open, int Retiring, int Uses, int Prepared, int Waiters,
        int Pending, int Producers, bool OfferPending, int ClientStreams, int ServerStreams);

    private static async Task<CombinedCounters> CombinedCountersAsync(string stage, SharpLinkClient client,
        ClientConnection conn, ServerConnectionState sc, int prepared, int held, CancellationToken ct)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watch.CancelAfter(TimeSpan.FromSeconds(2));
        var expected = new CombinedCounters(prepared, held, held, prepared, 0, held, 0, false, 0, 0);
        CombinedCounters Read()
        {
            var (open, retiring, users) = sc.NStudyBindings.Snapshot();
            return new(open, retiring, users, conn.Session.NStudyPreparedCount, client.NStudyPrepareWaitersForDiagnostics,
                conn.PendingCalls.ActiveCount, conn.PendingCalls.NStudyProducerCount, conn.Session.NStudyOfferPendingForDiagnostics,
                conn.Session.StreamManager.ActiveStreamCount, sc.Session.StreamManager.ActiveStreamCount);
        }
        try
        {
            await WaitForNStudyAsync(() => Read() == expected, watch.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{stage}: expected {expected}, saw {Read()}");
        }
        return expected;
    }

    private static async Task RequireCombinedCollectionAsync(List<WeakReference> a, List<WeakReference> b, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!a.Any(x => x.IsAlive) && !b.Any(x => x.IsAlive))
                return;
            await Task.Delay(20, ct);
        }
        Ensure(false, $"a retired context is still strongly held: server alive {a.Count(x => x.IsAlive)}, client alive {b.Count(x => x.IsAlive)}");
    }

    private sealed record CombinedProxy(object Service, RpcLocalContract Local, Func<TimeSpan, CancellationToken, ValueTask> Prepare);
    private static CombinedProxy CreateCombinedProxy<T>(SharpLinkClient client) where T : IService
    {
        var proxy = client.CreateProxy<T>();
        return new(proxy.Service, client.GetLocalContract<T>(), proxy.PrepareAsync);
    }

    private sealed class CombinedClientLoad(int step) : IDisposable
    {
        private readonly PluginBundle _bundle = PluginBundle.Load($"combined-client-{step}", loadService: false);
        private CombinedProxy? _proxy;
        internal Assembly Assembly => _bundle.ContractAssembly;
        internal CombinedProxy Proxy => _proxy ?? throw new InvalidOperationException("Missing client generation proxy");
        internal void Capture(SharpLinkClient client)
            => _proxy = (CombinedProxy)typeof(RuntimeAssemblyIntegrationTests)
                .GetMethod(nameof(CreateCombinedProxy), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(_bundle.ContractType).Invoke(null, [client])!;
        internal ValueTask<int> Call(string method, params object?[] arguments)
            => InvokeValueTaskAsync<int>(Proxy.Service, _bundle.ContractType, method, arguments);
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference Retire()
        {
            _proxy = null;
            return _bundle.Unload();
        }
        public void Dispose()
        {
            _proxy = null;
            _bundle.Dispose();
        }
    }

    private sealed class CombinedServiceLoad : IDisposable
    {
        private SharedContractServiceLoadContext? _loader;
        private Assembly? _assembly;
        private Type? _type;
        internal CombinedServiceLoad(Assembly fixedContract)
        {
            _loader = new SharedContractServiceLoadContext(fixedContract);
            _assembly = _loader.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(fixedContract.Location)!,
                "SharpLink.DynamicPlugin.Services.dll"));
            _type = _assembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
        }
        internal Assembly Assembly => _assembly ?? throw new ObjectDisposedException(nameof(CombinedServiceLoad));
        internal Type ServiceType => _type ?? throw new ObjectDisposedException(nameof(CombinedServiceLoad));
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference Retire()
        {
            var weak = new WeakReference(_loader);
            Dispose();
            return weak;
        }
        public void Dispose()
        {
            var loader = _loader;
            _loader = null;
            _type = null;
            _assembly = null;
            loader?.Unload();
        }
    }

}
