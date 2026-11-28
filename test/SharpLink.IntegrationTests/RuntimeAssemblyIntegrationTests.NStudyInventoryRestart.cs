using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyMissedVersionNoticeRecoversAcrossActualServerRestart(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        using var contracts = PluginBundle.Load($"n-inventory-restart-{transport}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "real generated contract manifests");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var aCid = manifest!.Contracts.Single(x => x.ContractType == contracts.ContractType).ContractId;
        var bCid = staticManifest!.Contracts.Single(x => x.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!, "SharpLink.DynamicPlugin.Services.dll");
        var r1Loader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var r2Loader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var restartedLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var seq = 0;
        void Record(string edge, NStudyInventorySnapshot snapshot, NStudyObservedTarget selection, NStudyAck? bound = null)
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
                publisher = snapshot.PublisherEpoch,
                registration = selection.Target.Registration,
                publication = selection.Target.Publication,
                binding = bound?.BindingId,
                hash = selection.Hash.ToString()
            }));

        try
        {
            var r1Assembly = r1Loader.LoadFromAssemblyPath(path);
            var r2Assembly = r2Loader.LoadFromAssemblyPath(path);
            var restartedAssembly = restartedLoader.LoadFromAssemblyPath(path);
            var r2Type = r2Assembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var restartedType = restartedAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var memory = transport == "shared-memory" ? $"sharplink-n-inv-restart-{Guid.NewGuid():N}" : null;
            await using var originalProvider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var firstBuilder = ConfigureEndpoint(originalProvider, port: 0);
            var port = memory is null ? ((IPEndPoint)firstBuilder.Transport!.LocalEndPoint!).Port : 0;
            await using var original = (SharpLinkServer)firstBuilder.Build();
            await using var client = NewNStudyClient(source, port, memory);
            original.NStudyVersionCoordinationMode = client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
            Ensure(original.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   original.RegisterAssembly(r1Assembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "both endpoints own the original compatible deployment");
            await original.StartAsync(ct);
            await client.ConnectAsync(ct);
            var oldSession = NStudyClientSession(client);
            var oldPeer = NStudyServerConnection(original);
            var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
            var a = receiver.Service;
            var b = client.CreateNStudyContractSite<IShutdownCleanupProbe>();
            var initial = await client.QueryNStudyRemoteInventoryAsync(ct);
            var r1 = initial.Select(aCid);
            var originalA = await receiver.PrepareExact(r1, TimeSpan.FromSeconds(5), ct);
            var originalB = await b.PrepareExactAsync(initial.Select(bCid), TimeSpan.FromSeconds(5), ct);
            var reverseBefore = await original.QueryNStudyClientInventoryAsync(oldPeer, ct);
            Ensure(initial.Authority == reverseBefore.Authority &&
                   initial.Authority.PhysicalSession == oldPeer.NStudyPhysicalIdentity &&
                   oldPeer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? oldSession.RemoteEndPoint is IPEndPoint : oldSession.Input is SharedMemoryPipeReader) &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 1, ct) == 2 &&
                   await b.Service.TouchAsync(1, ct) == 101,
                "actual original physical Session, bidirectional inventory, and two real grants");
            Record("OriginalAuthorityAndR1BindingBeforeMissedNotice", initial, r1, originalA);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new ConcurrentQueue<NStudyVersionNotice>();
            var observer = client.ConfigureNStudyVersionObserver(async (notice, _) =>
            {
                delivered.Enqueue(notice);
                if (entered.TrySetResult())
                    await releaseObserver.Task;
            });

            (Task<NStudyClientActivation?> Client, Task<NStudyServerActivation> Server) Attempt(bool approve)
                => (client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server,
                        (_, _) => ValueTask.FromResult(approve), ct),
                    original.StageNStudyReplacementAsync(oldPeer, r1Assembly, r2Assembly, aCid, NStudyInitiator.Server,
                        static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(10), ct));

            for (var index = 0; index <= NStudyVersionObservationQueue.Capacity / 2; index++)
            {
                var refused = Attempt(approve: false);
                Ensure(await refused.Client.WaitAsync(ct) is null &&
                       !(await refused.Server.WaitAsync(ct)).Replacement.Succeeded,
                    "ordinary declined negotiation keeps R1 while filling the notification queue");
                await entered.Task.WaitAsync(ct);
            }
            var full = observer.Snapshot();
            Ensure(full.Active && full.Pending == NStudyVersionObservationQueue.Capacity && full.Dropped == 1,
                "one actual slow observer has a full pending queue");

            var switched = Attempt(approve: true);
            var active = await switched.Client.WaitAsync(ct) ?? throw new InvalidOperationException("Expected real old-server R2.");
            EnsureReplacementReleased((await switched.Server.WaitAsync(ct)).Replacement, "first server really publishes R2");
            var previous = await client.QueryNStudyRemoteInventoryAsync(ct);
            var r2 = previous.Select(aCid);
            Ensure(previous.Authority == initial.Authority && previous.Revision > initial.Revision &&
                   r2.Target == active.Binding.Target && r2.Target != r1.Target &&
                   observer.Snapshot().Dropped == full.Dropped + 2 && delivered.Count == 1 &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 2, ct) == 3 &&
                   await b.Service.TouchAsync(2, ct) == 102,
                "client application notifications drop this successful R2 while the registry and prepared RPC are real");
            Record("R2CommittedButItsApplicationNoticesWereDropped", previous, r2, active.Binding);

            await original.StopAsync(TimeSpan.FromSeconds(2), ct);
            await WaitForNStudyAsync(() => oldSession.LifetimeToken.IsCancellationRequested && client.ReadyConnectionCount == 0 &&
                client.State == SharpLinkConnectionState.Reconnecting, ct);
            var nextReady = client.WaitForReadinessAsync(1, ct).AsTask();
            Ensure(!nextReady.IsCompleted && NStudyCount(r2Type, "Disposed") == 1,
                "original runtime is gone and the same client has a genuinely pending reconnect");
            Record("OriginalServerStopsAndClientLosesItsPhysicalSession", previous, r2);

            await using var replacementProvider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            await using var replacement = (SharpLinkServer)ConfigureEndpoint(replacementProvider, port).Build();
            replacement.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
            Ensure(replacement.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   replacement.RegisterAssembly(restartedAssembly).Succeeded, "replacement deploys exact fresh server module");
            await replacement.StartAsync(ct);
            Ensure((await nextReady.WaitAsync(ct)).ReadyConnections == 1, "the same client reconnects to the replacement listener");
            var freshSession = NStudyClientSession(client);
            var freshPeer = NStudyServerConnection(replacement);
            Ensure(!ReferenceEquals(freshSession, oldSession) && freshPeer.AuthenticationContext?.Subject == "n-validation" &&
                   freshSession.NStudyPreparedCount == 0 && freshSession.NStudyBindOperationForDiagnostics == 0 &&
                   freshPeer.NStudyBindings.Snapshot() == (0, 0, 0) && NStudyCount(restartedType, "Created") == 0,
                "actual new authenticated Ready Session cannot inherit any prepared registration or service execution");

            await ExpectPostRestartUnavailableAsync(async () =>
            {
                _ = await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 3, ct);
            });
            await ExpectPostRestartUnavailableAsync(async () => { _ = await b.Service.TouchAsync(3, ct); });
            var fresh = await client.QueryNStudyRemoteInventoryAsync(ct);
            var restarted = fresh.Select(aCid);
            var locallyReported = await replacement.QueryNStudyClientInventoryAsync(freshPeer, ct);
            Ensure(fresh.Authority.Server != previous.Authority.Server &&
                   fresh.Authority.RegistryEpoch != previous.Authority.RegistryEpoch &&
                   fresh.Authority.PhysicalSession != previous.Authority.PhysicalSession &&
                   fresh.Authority.PhysicalSession == freshPeer.NStudyPhysicalIdentity &&
                   fresh.PublisherEpoch != previous.PublisherEpoch && fresh.Revision < previous.Revision &&
                   restarted.Hash == r2.Hash && restarted.Target != r2.Target && restarted.Target != r1.Target &&
                   restarted.Target == replacement.ReadNStudyTarget(restartedAssembly, aCid) &&
                   locallyReported.Authority == fresh.Authority &&
                   locallyReported.PublisherEpoch == reverseBefore.PublisherEpoch &&
                   locallyReported.Revision == reverseBefore.Revision &&
                   locallyReported.Select(aCid).Target == reverseBefore.Select(aCid).Target,
                "bilateral read distinguishes restarted registry revision from the unchanged client's local publication");
            Record("FreshInventoryReportsNewServerEpochAndLowerRevisionWithoutBinding", fresh, restarted);

            await ExpectPriorObservationRejectedAsync(r1);
            await ExpectPriorObservationRejectedAsync(r2);
            Ensure(freshSession.NStudyBindOperationForDiagnostics == 0 &&
                   freshPeer.NStudyBindings.Snapshot() == (0, 0, 0) && NStudyCount(restartedType, "Created") == 0,
                "old real receipts from either prior generation fail before any Bind or dispatch");
            Record("NewAuthorityRejectsBothPreviousRealInventoryObservations", fresh, restarted);

            var newA = await receiver.PrepareExact(restarted, TimeSpan.FromSeconds(5), ct);
            var newB = await b.PrepareExactAsync(fresh.Select(bCid), TimeSpan.FromSeconds(5), ct);
            Ensure(newA.Authority == fresh.Authority && newB.Authority == fresh.Authority &&
                   newA.BindingId == originalA.BindingId && newB.BindingId == originalB.BindingId &&
                   newA.Target == restarted.Target && freshSession.NStudyBindOperationForDiagnostics == 2 &&
                   freshPeer.NStudyBindings.Snapshot() == (2, 0, 0) &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
                   await b.Service.TouchAsync(4, ct) == 104 && NStudyCount(restartedType, "Created") == 1,
                "fresh exact preparation is the only authority; stock numeric slots may repeat on the new physical Session");
            Record("ExplicitFreshBindingsRestoreSameProxiesWithOrdinaryTokenReuse", fresh, restarted, newA);

            releaseObserver.TrySetResult();
            await WaitForNStudyAsync(() => observer.Snapshot() is { Active: false, Pending: 0 }, ct);
            Ensure(delivered.Count == NStudyVersionObservationQueue.Capacity + 1 &&
                   delivered.All(notice => notice.Change != active.Change) &&
                   freshSession.NStudyBindOperationForDiagnostics == 2 && await b.Service.TouchAsync(5, ct) == 105,
                "draining the real observer cannot replay the missed R2 notice, change the new authority, or create a Bind");
            Record("NoReplayOfLostR2NoticesNeededForNewRegistryRecovery", fresh, restarted, newA);

            SharpLinkServerBuilder ConfigureEndpoint(IServiceProvider provider, int port)
            {
                var builder = NewNStudyServer(source, provider);
                if (memory is null)
                    builder.UseTcp(port, IPAddress.Loopback);
                else
                    builder.UseSharedMemory(memory);
                return builder;
            }

            async Task ExpectPriorObservationRejectedAsync(NStudyObservedTarget oldObservation)
            {
                try
                {
                    _ = await receiver.PrepareExact(oldObservation, TimeSpan.FromSeconds(5), ct);
                    throw new InvalidOperationException("A prior real observation must not be borrowed across restart.");
                }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition) { }
            }
        }
        finally
        {
            releaseObserver.TrySetResult();
            r1Loader.Unload();
            r2Loader.Unload();
            restartedLoader.Unload();
        }
    }

    private static async Task ExpectPostRestartUnavailableAsync(Func<Task> request)
    {
        try
        {
            await request();
            throw new InvalidOperationException("The reconnected proxy must not call before exact preparation.");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
    }
}
