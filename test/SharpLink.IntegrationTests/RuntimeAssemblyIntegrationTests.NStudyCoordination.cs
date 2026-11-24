using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client")]
    [Arguments("tcp", "server")]
    [Arguments("shared-memory", "client")]
    [Arguments("shared-memory", "server")]
    public async Task NStudyManualCoordinationUsesActualPreparedGroupOnTheSameSession(string transport, string initiator)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var choice = initiator == "client" ? NStudyInitiator.Client : NStudyInitiator.Server;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-manual-{transport}-{initiator}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "actual compatible generated manifests");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-manual-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName);
        server.NStudyVersionCoordinationMode = client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        Type? oldType = null;
        var serverDecision = new NStudyManualDecision();
        var clientDecision = new NStudyManualDecision();
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "only R1 and compatible local contract are registered");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var site = CreateNStudyExactGenerated(client, contracts.ContractType);
            var a = site.Service;
            var a1 = await site.PrepareExact(new(bBinding.Authority, aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(oldAssembly, aCid)), TimeSpan.FromSeconds(5), ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var clientContractModule = GetDynamicModule(client, contracts.ContractAssembly);
            var observed = await server.QueryNStudyClientInventoryAsync(peer, ct);
            var observedClientRegistration = observed.Select(aCid);
            Ensure(session.SupportsNStudyCoordination && peer.Session.SupportsNStudyCoordination &&
                   a1.Authority == bBinding.Authority && a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   observed.Authority == a1.Authority && observedClientRegistration.Hash == manifest.RpcAssemblyHash &&
                   observedClientRegistration.Target.Registration == clientContractModule.NStudyRegistration &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "both selected authenticated peers negotiated Manual on their real Session");
            trace.Record("ManualCapabilityNegotiatedOnTheBoundPhysicalSession", a1, bBinding);

            // Normal application choice: decline a staged version, then allow a new
            // attempt. No business traffic is retargeted by the declaration alone.
            var firstClient = client.NegotiateNStudyReplacementAsync(choice,
                static (_, _) => ValueTask.FromResult(false), ct);
            var firstServer = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, aCid, choice,
                static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);
            Ensure(await firstClient.WaitAsync(ct) is null, "ordinary local manual decline produces no activation");
            var firstOutcome = await firstServer.WaitAsync(ct);
            var declined = firstOutcome.Change ?? throw new InvalidOperationException("The declined candidate was not staged.");
            Ensure(!firstOutcome.Replacement.Succeeded &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId && NStudyCount(newType, "Created") == 0 &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 6, ct) == 7,
                "decline leaves exact R1 and its original route untouched");
            trace.Record("NormalManualDeclineKeepsTheOriginalBinding", a1);

            var oldCall = InvokeValueTaskAsync<int>(a, contracts.ContractType, "BlockAsync", ct).AsTask();
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
            var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, aCid, choice,
                async (change, policyToken) =>
                {
                    var snapshot = await server.QueryNStudyClientInventoryAsync(peer, policyToken);
                    var local = snapshot.Select(change.Contract);
                    Ensure(snapshot.Authority == change.Authority && local.Hash == change.Hash &&
                           local.Target.Registration == clientContractModule.NStudyRegistration,
                        "server policy can await the current authenticated peer inventory before approving");
                    return await serverDecision.DecideAsync(change, policyToken);
                }, TimeSpan.FromSeconds(15), ct);
            if (choice == NStudyInitiator.Client)
            {
                Ensure(!serverDecision.Seen.IsCompleted && await b.TouchAsync(1, ct) == 101,
                    "the true server stays at availability until the client elects to propose");
            }
            var negotiating = client.NegotiateNStudyReplacementAsync(choice, async (change, policyToken) =>
            {
                Ensure(await b.TouchAsync(5, policyToken) == 105 &&
                       session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
                    "client policy can await ordinary unrelated RPC on its original prepared route");
                return await clientDecision.DecideAsync(change, policyToken);
            }, ct);
            var offered = await serverDecision.Seen.WaitAsync(ct);
            Ensure(offered.Initiator == choice && offered.Id > declined.Id &&
                   offered.Target != declined.Target && offered.Original == a1.Target &&
                   offered.Contract == aCid && offered.Hash == manifest.RpcAssemblyHash &&
                   offered.Authority == a1.Authority && !clientDecision.Seen.IsCompleted &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target && await b.TouchAsync(2, ct) == 102,
                "the actual proposal neither activates R2 nor blocks unrelated B while server policy is pending");
            trace.Record("RealProposalAwaitsServerLocalApproval", a1);

            serverDecision.Approve();
            Ensure(await clientDecision.Seen.WaitAsync(ct) == offered &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId &&
                   await b.TouchAsync(3, ct) == 103 &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 8, ct) == 9 &&
                   !oldCall.IsCompleted, "server approval alone neither cuts R1 nor opens R2 before client approval");
            trace.Record("BothPoliciesCanAwaitWorkOnTheOriginalSession", a1, bBinding);
            trace.Record("ServerApprovalStillWaitsForPreparedClientDecision", a1);

            var priorBind = session.NStudyBindOperationForDiagnostics;
            clientDecision.Approve();
            var activated = await negotiating.WaitAsync(ct) ?? throw new InvalidOperationException("Expected activation.");
            Ensure(activated.Change == offered &&
                   activated.LocalRegistration == clientContractModule.NStudyRegistration &&
                   activated.Binding.Authority == a1.Authority &&
                   activated.Binding.Target == server.ReadNStudyTarget(newAssembly, aCid) &&
                   activated.Binding.Target == offered.Target &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "the new real R2 Bind follows activation of the one approved candidate");
            var a2 = activated.Binding;
            Ensure(!replacing.IsCompleted && !oldCall.IsCompleted &&
                   peer.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 10, ct) == 11 &&
                   NStudyCount(newType, "Created") == 1 && await b.TouchAsync(4, ct) == 104 &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "new calls reach exact R2 and B without moving the physically active R1 call");
            trace.Record("ApprovedExactR2ResumesWhileOldR1AndBRemainLive", a1, a2);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await oldCall.WaitAsync(ct) == 42, "the active original R1 result remains valid");
            var final = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(final.Replacement, "only the old coordinated group retires");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            Ensure(final.Change == offered && final.PreparedClientRegistration == activated.LocalRegistration &&
                   final.PreparedClientRegistration == observedClientRegistration.Target.Registration &&
                   NStudyCount(oldType, "Disposed") == 1 && ReferenceEquals(session, NStudyClientSession(client)) &&
                   GetDynamicModule(client, contracts.ContractAssembly) == clientContractModule &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 12, ct) == 13 &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "both participants report the same exact ticket; local client registration/proxy did not get replaced");
            trace.Record("OriginalR1ExitsOnceAndBothReportTheSameActivation", a1, a2);
        }
        finally
        {
            serverDecision.Approve();
            clientDecision.Approve();
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private sealed class NStudyManualDecision
    {
        private readonly TaskCompletionSource<NStudyChange> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<NStudyChange> Seen => _seen.Task;
        internal void Approve() => _approval.TrySetResult(true);
        internal void Decline() => _approval.TrySetResult(false);
        internal async ValueTask<bool> DecideAsync(NStudyChange change, CancellationToken ct)
        {
            _seen.TrySetResult(change);
            return await _approval.Task.WaitAsync(ct);
        }
    }
}
