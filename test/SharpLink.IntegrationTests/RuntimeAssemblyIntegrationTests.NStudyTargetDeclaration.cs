using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task NStudyDefaultVersionOperationsSelectOnlyTheSoleReadyClusterConnection(bool useResolver, int ready)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = guard.Token;
        using var target = PluginBundle.Load($"n-cluster-sole-{useResolver}-{ready}", loadService: false);
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(NStudyAuthenticationPayload)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("n-validation"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication().UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        await using var server = serverBuilder.Build();
        server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);

        var endpoints = Enumerable.Range(0, useResolver ? ready : 2).Select(index => new SharpLinkEndpoint
        {
            Id = $"peer-{index}",
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        }).ToArray();
        var builder = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(5))
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(NStudyAuthenticationPayload)));
        if (useResolver)
        {
            var snapshot = new SharpLinkEndpointSnapshot(1, endpoints);
            builder.UseEndpointResolver(new DelegateSharpLinkEndpointResolver(_ => ValueTask.FromResult(snapshot)),
                SharpLinkTransportFactories.Sockets());
        }
        else
        {
            builder.UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets()).UseCluster(options =>
            {
                options.MinReadyEndpoints = ready;
                options.MaxConnections = ready;
                options.MaxConnectionsPerEndpoint = 1;
            });
        }
        await using var client = builder.Build();
        client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        Ensure(client.RegisterAssembly(target.ContractAssembly).Succeeded, "the application installs the declaration locally");
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(target.ContractAssembly, out var manifest).Succeeded,
            "read the generated local contract");
        var cid = manifest!.Contracts.Single(contract => contract.ContractType == target.ContractType).ContractId;
        var local = client.GetLocalContract(target.ContractAssembly, cid);

        await ExpectUnavailable(client.QueryPeerVersionInventoryAsync(ct).AsTask());
        await ExpectUnavailable(client.DeclareVersionTargetAsync(local, ct).AsTask());
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        await client.WaitForReadinessAsync(ready, ct);
        var selectors = client.GetVersionConnections();
        Ensure(selectors.Count == ready, "the cluster publishes its actual Ready connections");
        if (ready == 1)
        {
            var inventory = await client.QueryPeerVersionInventoryAsync(ct);
            var declaration = await client.DeclareVersionTargetAsync(local, ct);
            var selected = await client.QueryPeerVersionInventoryAsync(selectors[0], ct);
            Ensure(inventory.Session == selected.Session && inventory.Session == declaration.Session &&
                   declaration.Status == SharpLinkVersionDeclarationStatus.Missing,
                "both unspecific operations use the unique physical connection without installing the target");
        }
        else
        {
            await ExpectUnavailable(client.QueryPeerVersionInventoryAsync(ct).AsTask());
            await ExpectUnavailable(client.DeclareVersionTargetAsync(local, ct).AsTask());
            var sessions = new HashSet<SharpLinkVersionSession>();
            foreach (var selector in selectors)
            {
                var inventory = await client.QueryPeerVersionInventoryAsync(selector, ct);
                var declaration = await client.DeclareVersionTargetAsync(local, selector, ct);
                Ensure(inventory.Session is { } session && declaration.Session == session &&
                       declaration.Status == SharpLinkVersionDeclarationStatus.Missing && sessions.Add(session),
                    "the explicit selectors keep each actual physical connection distinct");
            }
        }

        async Task ExpectUnavailable(Task operation)
        {
            try { await operation.WaitAsync(ct); }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { return; }
            throw new InvalidOperationException("A default operation must refuse without a unique Ready connection.");
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyClientDeclaresARealLocalTargetBeforeServerDeployment(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var target = PluginBundle.Load($"n-target-declaration-{transport}");
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly,
            out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(target.ContractAssembly, out var targetManifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "real generated declarations");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = targetManifest!.Contracts.Single(c => c.ContractType == target.ContractType).ContractId;

        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        var memory = transport == "shared-memory" ? $"sharplink-n-target-{Guid.NewGuid():N}" : null;
        if (memory is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memory);
        var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memory);
        server.NStudyVersionCoordinationMode = NStudyCoordinationMode.Automatic;
        client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
        Ensure(client.RegisterAssembly(target.ContractAssembly).Succeeded, "client application deployed the target locally");
        var local = GetDynamicModule(client, target.ContractAssembly);
        var declaration = new NStudyTargetDeclaration(aCid, targetManifest.RpcAssemblyHash,
            new(local.NStudyRegistration, local.NStudyPublication));

        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var session = NStudyClientSession(client);
        var peer = NStudyServerConnection(server);
        Ensure(session.SupportsNStudyTargetDeclaration && peer.Session.SupportsNStudyTargetDeclaration &&
               peer.AuthenticationContext?.Subject == "n-validation" &&
               (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
            "actual transport and both endpoints negotiate this optional control independently of business Bind");

        // The client speaks first: no server staging call, no server service entry or previous Bind.
        var missing = await client.DeclareNStudyTargetAsync(target.ContractType, ct);
        Ensure(missing.Declaration == declaration && missing.Status == NStudyTargetStatus.Missing &&
               missing.Target == default && missing.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
               peer.NStudyBindings.Snapshot() == (0, 0, 0) && session.NStudyBindOperationForDiagnostics == 0 &&
               session.HasNStudyAuthority(missing.Authority),
            "the actual server reports the Client's local target absent without installing any grant");
        var before = await client.QueryNStudyRemoteInventoryAsync(ct);
        Ensure(before.Authority == missing.Authority && before.Entries.All(entry => entry.Contract != aCid),
            "the declaration did not install or stage the remote target");
        trace.Record("ClientDeclarationLeadsBeforeAnyServerTargetOrBind");

        var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
        var bBindCount = session.NStudyBindOperationForDiagnostics;
        var receiver = CreateNStudyExactGenerated(client, target.ContractType);
        var actualProxy = receiver.Service;

        server.NStudyVersionCoordinationMode = NStudyCoordinationMode.Disabled;
        var disabled = await client.DeclareNStudyTargetAsync(target.ContractType, ct);
        Ensure(disabled.Status == NStudyTargetStatus.Disabled && disabled.Authority == missing.Authority &&
               disabled.Declaration == declaration && disabled.Target == default && await b.TouchAsync(1, ct) == 101,
            "a live Disabled peer answers explicitly without an application participant or disconnect");
        trace.Record("DisabledPeerRefusesWithoutSeparateServerParticipant", bBinding);

        // The application first deploys an existing alternate version with the
        // same contract ID, but a genuinely different generated assembly hash.
        using (var alternate = PluginBundle.Load($"n-other-target-{transport}", replacement: true))
        {
            var alternateLoad = SharpLinkAssemblyManifestLoader.TryLoad(alternate.ContractAssembly, out var alternateManifest);
            Ensure(alternateLoad.Succeeded && alternateManifest!.RpcAssemblyHash != declaration.Hash &&
                   alternateManifest.Contracts.Single(c => c.ContractType == alternate.ContractType).ContractId == aCid &&
                   server.RegisterAssembly(alternate.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(alternate.ServiceAssembly).Succeeded,
                "a real independently built alternate version is installed");
            server.NStudyVersionCoordinationMode = NStudyCoordinationMode.Automatic;
            var other = await client.DeclareNStudyTargetAsync(target.ContractType, ct);
            Ensure(other.Status == NStudyTargetStatus.Incompatible && other.Declaration == declaration &&
                   other.Authority == missing.Authority && other.Target == default &&
                   peer.NStudyBindings.Snapshot() == (1, 0, 0) && await b.TouchAsync(5, ct) == 105,
                "same ID with a different generated hash cannot grant the requested target");
            trace.Record("ActuallyInstalledDifferentHashRefusesDeclaredTarget", bBinding);
            Ensure((await server.UnregisterAssemblyAsync(alternate.ServiceAssembly, TimeSpan.FromSeconds(5), ct))
                       .ReferencesReleased &&
                   (await server.UnregisterAssemblyAsync(alternate.ContractAssembly, TimeSpan.FromSeconds(5), ct))
                       .ReferencesReleased, "server application retires the alternate artifacts without preparing them");
        }

        // Only the server application makes the declared exact version exist.
        Ensure(server.RegisterAssembly(target.ContractAssembly).Succeeded &&
               server.RegisterAssembly(target.ServiceAssembly).Succeeded,
            "server application independently installs the real target generation");
        var deployed = await client.QueryNStudyRemoteInventoryAsync(ct);
        var exact = deployed.Select(aCid);
        Ensure(deployed.Authority == missing.Authority && deployed.Revision > before.Revision &&
               exact.Hash == declaration.Hash && exact.Target == server.ReadNStudyTarget(target.ServiceAssembly, aCid),
            "an actual server registry publication, not a declaration, makes the target observable");

        server.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
        var notParticipating = await client.DeclareNStudyTargetAsync(target.ContractType, ct);
        Ensure(notParticipating.Declaration == declaration && notParticipating.Authority == missing.Authority &&
               notParticipating.Status == NStudyTargetStatus.ApprovalRequired && notParticipating.Target == default &&
               await b.TouchAsync(2, ct) == 102,
            "without a server application participant Manual requires approval, never reports readiness");
        trace.Record("ManualPeerWithoutParticipantReturnsApprovalRequired", bBinding);

        server.NStudyVersionCoordinationMode = NStudyCoordinationMode.Automatic;
        var observed = await client.DeclareNStudyTargetAsync(target.ContractType, ct);
        Ensure(observed.Declaration == declaration && observed.Status == NStudyTargetStatus.Observed &&
               observed.Authority == exact.Authority && observed.Target == exact.Target &&
               target.GetStaticInt("Created") == 0 &&
               peer.NStudyBindings.Snapshot() == (1, 0, 0) && session.NStudyPreparedCount == 1 &&
               session.NStudyBindOperationForDiagnostics == bBindCount,
            "exact registry observation establishes neither activation nor a second Binding");
        trace.Record("RealDeploymentAllowsAdvisoryObservationWithoutOpeningAdmission", bBinding);

        var binding = await receiver.PrepareExact(
            new(observed.Authority, observed.Declaration.Contract, observed.Declaration.Hash, observed.Target),
            TimeSpan.FromSeconds(5), ct);
        Ensure(binding.Authority == missing.Authority && binding.Target == exact.Target &&
               session.NStudyBindOperationForDiagnostics == bBindCount + 1 &&
               peer.NStudyBindings.Snapshot() == (2, 0, 0) &&
               ReferenceEquals(session, NStudyClientSession(client)) &&
               await InvokeValueTaskAsync<int>(actualProxy, target.ContractType, "UnaryAsync", 4, ct) == 5 &&
               await b.TouchAsync(3, ct) == 103 &&
               target.GetStaticInt("Created") == 1 &&
               session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
            "only the explicit exact prepared proxy calls the real installed service on the original Session");
        trace.Record("ExplicitBindingAfterRealDeploymentUsesInstalledServiceAndKeepsB", binding, bBinding);
    }
}
