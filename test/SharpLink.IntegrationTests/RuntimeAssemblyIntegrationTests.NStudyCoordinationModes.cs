using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "both-disabled")]
    [Arguments("tcp", "client-only")]
    [Arguments("tcp", "server-only")]
    [Arguments("tcp", "automatic-client")]
    [Arguments("tcp", "automatic-server")]
    [Arguments("shared-memory", "both-disabled")]
    [Arguments("shared-memory", "client-only")]
    [Arguments("shared-memory", "server-only")]
    [Arguments("shared-memory", "automatic-client")]
    [Arguments("shared-memory", "automatic-server")]
    public async Task NStudyCoordinationModesHonorLocalDeploymentAndOrdinaryRetirement(string transport, string scenario)
    {
        var automatic = scenario is "automatic-client" or "automatic-server";
        var clientMode = automatic || scenario == "client-only"
            ? NStudyCoordinationMode.Automatic : NStudyCoordinationMode.Disabled;
        var serverMode = automatic ? NStudyCoordinationMode.Automatic : scenario == "server-only"
            ? NStudyCoordinationMode.Manual : NStudyCoordinationMode.Disabled;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-modes-{transport}-{scenario}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "actual generated manifests");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-modes-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName);
        server.NStudyVersionCoordinationMode = serverMode;
        client.NStudyVersionCoordinationMode = clientMode;

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
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "R1 and compatible contract register");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var bBinding = await client.PrepareNStudyContractAsync(bCid, staticManifest.RpcAssemblyHash, default, ct);
            var a1 = await client.PrepareNStudyContractAsync(aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(oldAssembly, aCid), ct);
            var connection = NStudyClientConnection(client);
            var peer = NStudyServerConnection(server);
            Ensure(connection.Session.NStudyLocalCoordinationMode == clientMode &&
                   peer.Session.NStudyLocalCoordinationMode == serverMode &&
                   connection.Session.SupportsNStudyCoordination == automatic &&
                   peer.Session.SupportsNStudyCoordination == automatic &&
                   a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? connection.Session.RemoteEndPoint is IPEndPoint :
                       connection.Session.Input is SharedMemoryPipeReader), "actual handshake captures and intersects local modes");
            trace.Record("PhysicalHandshakeCapturesLocalCoordinationModes", a1, bBinding);
            var fixture = new NStudyModePeer(server, client, connection, peer, a1, bBinding,
                oldAssembly, newAssembly, oldType, newType);
            if (automatic)
                await VerifyNStudyAutomaticLocalDeploymentAsync(
                    fixture, contracts, scenario, trace, ct);
            else
                await VerifyNStudyDisabledPeerRetirementAsync(
                    fixture, contracts.ContractType, trace, ct);
        }
        finally
        {
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static async Task VerifyNStudyDisabledPeerRetirementAsync(
        NStudyModePeer fixture, Type contract, NStudyTrace trace, CancellationToken ct)
    {
        var (server, client, connection, peer, a1, bBinding, oldAssembly, newAssembly, oldType, newType) = fixture;
        var a = GetProxy(client, contract);
        var b = client.Get<IShutdownCleanupProbe>();
        var oldCall = InvokeValueTaskAsync<int>(a, contract, "BlockAsync", ct).AsTask();
        await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);

        await ExpectNStudyCoordinationUnavailableAsync(client.NegotiateNStudyReplacementAsync(
            NStudyInitiator.Client, approveLocally: null, ct));
        await ExpectNStudyCoordinationUnavailableAsync(server.StageNStudyReplacementAsync(
            peer, oldAssembly, newAssembly, a1.Contract, NStudyInitiator.Server, approveLocally: null,
            TimeSpan.FromSeconds(15), ct));
        Ensure(connection.Session.CaptureNStudyBinding(a1.Contract) == a1.BindingId &&
               server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
               NStudyCount(newType, "Created") == 0 && await b.TouchAsync(1, ct) == 101,
            "one side alone cannot opt this Session into version coordination");
        trace.Record("UnnegotiatedCoordinationLeavesBothExistingRoutesUsable", a1, bBinding);

        // Disabled coordination must not suppress a normal local registry
        // retirement or allow later business to reach the old registration.
        var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
        await WaitForNStudyAsync(() => connection.Session.NStudyPreparedCount == 1, ct);
        Ensure(!replacing.IsCompleted && !oldCall.IsCompleted &&
               peer.NStudyBindings.Snapshot() == (1, 1, 1) && await b.TouchAsync(2, ct) == 102,
            "A's ordinary retirement and B availability are independent of the optional feature");
        trace.Record("OrdinaryRegistryCutStillRetiresOnlyAOnDisabledSession", a1, bBinding);
        var a2 = await client.PrepareNStudyContractAsync(a1.Contract, a1.Hash,
            server.ReadNStudyTarget(newAssembly, a1.Contract), ct);
        Ensure(a2.Authority == a1.Authority &&
               await InvokeValueTaskAsync<int>(a, contract, "UnaryAsync", 10, ct) == 11 &&
               NStudyCount(newType, "Created") == 1, "ordinary exact Bind remains available without coordination");
        oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
        Ensure(await oldCall.WaitAsync(ct) == 42, "R1 still returns in its original registration");
        EnsureReplacementReleased(await replacing.WaitAsync(ct), "ordinary retirement after physical R1 exit");
        await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
        Ensure(ReferenceEquals(connection.Session, NStudyClientSession(client)) &&
               connection.Session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
               NStudyCount(oldType, "Disposed") == 1, "original Session and unrelated binding survive");
        trace.Record("OrdinaryExactRebindWorksWithoutCoordination", a1, a2);
    }

    private static async Task VerifyNStudyAutomaticLocalDeploymentAsync(
        NStudyModePeer fixture, PluginBundle original, string scenario, NStudyTrace trace, CancellationToken ct)
    {
        var (server, client, connection, peer, a1, bBinding, oldAssembly, newAssembly, oldType, newType) = fixture;
        var initiator = scenario == "automatic-client" ? NStudyInitiator.Client : NStudyInitiator.Server;
        using var local = PluginBundle.Load($"n-auto-local-{scenario}", loadService: false);
        var input = new NStudyInput();
        try
        {
            var oldProxy = GetProxy(client, original.ContractType);
            var b = client.Get<IShutdownCleanupProbe>();
            var oldClientModule = GetDynamicModule(client, original.ContractAssembly);
            var oldServerModule = GetDynamicModule(server, oldAssembly);
            var oldFlow = CallNStudyInput(oldProxy, original.ContractType, "duplex", input, ct);
            await input.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 1, ct);
            Ensure(oldClientModule.RemainingStreams == 2 && oldServerModule.RemainingStreams == 1,
                "the original client producer and consumer and remote handler are physically active");

            // The application explicitly supplies a separate local Assembly
            // before proposing. Neither remote capability nor Automatic loads it.
            var localDrain = client.ReplaceAssemblyAsync(original.ContractAssembly, local.ContractAssembly,
                TimeSpan.FromSeconds(15), ct).AsTask();
            var newClientModule = GetDynamicModule(client, local.ContractAssembly);
            Ensure(local.ContractType != original.ContractType &&
                   newClientModule.NStudyRegistration != oldClientModule.NStudyRegistration &&
                   oldClientModule.State == SharpLinkDynamicModuleState.Draining &&
                   oldClientModule.RemainingStreams == 2 && !localDrain.IsCompleted &&
                   await b.TouchAsync(1, ct) == 101,
                "actual local registry publishes an independent generation while the old owners drain");
            trace.Record("ApplicationPublishesNewLocalContractWhileOldDuplexIsActive", a1);

            var negotiate = client.NegotiateNStudyReplacementAsync(initiator, approveLocally: null, ct);
            var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract,
                initiator, approveLocally: null, TimeSpan.FromSeconds(15), ct);
            var activated = await negotiate.WaitAsync(ct) ?? throw new InvalidOperationException("Expected automatic activation.");
            var a2 = activated.Binding;
            Ensure(activated.Change.Initiator == initiator && activated.Change.Original == a1.Target &&
                   activated.LocalRegistration == newClientModule.NStudyRegistration &&
                   a2.Authority == a1.Authority && a2.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                   connection.Session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
                   peer.NStudyBindings.Snapshot() == (2, 1, 1), "Automatic binds only the exact locally prepared generation");

            var newProxy = GetProxy(client, local.ContractType);
            var payloadType = local.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
            var payload = Activator.CreateInstance(payloadType)!;
            payloadType.GetProperty("Value")!.SetValue(payload, 7);
            payloadType.GetProperty("Label")!.SetValue(payload, "R2");
            payloadType.GetProperty("Values")!.SetValue(payload, new List<int> { 2, 3 });
            payloadType.GetProperty("Parent")!.SetValue(payload, payload);
            Ensure(newProxy.GetType() != oldProxy.GetType() &&
                   await InvokeValueTaskAsync<int>(newProxy, local.ContractType, "UsePayloadAsync", payload, ct) == 14 &&
                   NStudyCount(newType, "Created") == 1 && await b.TouchAsync(2, ct) == 102 &&
                   !oldFlow.IsCompleted && !localDrain.IsCompleted && !replacing.IsCompleted,
                "the actual new nominal type and generated payload Codec call R2 while original R1 and B remain live");
            trace.Record("AutomaticActivatesExactR2UsingNewLocalTypedProxy", a1, a2);

            input.ReleaseItems();
            await input.Disposing.WaitAsync(ct);
            Ensure(oldClientModule.RemainingStreams == 2 && oldServerModule.RemainingStreams == 1 &&
                   !localDrain.IsCompleted && !replacing.IsCompleted && NStudyCount(oldType, "Disposed") == 0,
                "both original client physical owners and server registration survive actual iterator cleanup");
            trace.Record("OriginalLocalAndRemoteStreamOwnersStayUntilPhysicalCleanup", a1, a2);
            input.ReleaseDisposal();
            Ensure((await oldFlow.WaitAsync(ct)).SequenceEqual(new[] { 6, 14 }), "original duplex finishes using R1");
            EnsureReplacementReleased(await localDrain.WaitAsync(ct), "original client generation drained");
            var final = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(final.Replacement, "original server generation drained");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0) &&
                connection.ActiveCallCount == 0, ct);
            Ensure(oldClientModule.State == SharpLinkDynamicModuleState.Released &&
                   oldClientModule.RemainingStreams == 0 && NStudyCount(oldType, "Disposed") == 1 &&
                   final.PreparedClientRegistration == newClientModule.NStudyRegistration && input.ExitCount == 1 &&
                   ReferenceEquals(connection.Session, NStudyClientSession(client)) && await b.TouchAsync(3, ct) == 103,
                "original physical owners close once on each side; only the application-selected module became live");
            trace.Record("OldClientAndServerGenerationsDrainOnOriginalSession", a1, a2);
        }
        finally
        {
            input.ReleaseItems();
            input.ReleaseDisposal();
        }
    }

    private static async Task ExpectNStudyCoordinationUnavailableAsync(Task operation)
    {
        try
        {
            await operation;
            throw new InvalidOperationException("An unnegotiated coordination attempt must not proceed.");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition) { }
    }

    private sealed record NStudyModePeer(SharpLinkServer Server, SharpLinkClient Client,
        ClientConnection Connection, ServerConnectionState Peer, NStudyAck A, NStudyAck B,
        Assembly OldAssembly, Assembly NewAssembly, Type OldType, Type NewType);
}
