using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

internal static partial class CoordinationSmoke
{
    internal static async Task RunMultiReadyAsync(string transport, string scenario, string pluginDirectory)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var deferPinned = scenario == "defer";
        var replaceLocal = scenario == "relocal";
        var mode = deferPinned ? SharpLinkVersionCoordinationMode.Manual : SharpLinkVersionCoordinationMode.Automatic;
        const string endpointId = "coordinated-pool";
        var memory = transport == "tcp" ? null : $"sl-pool-coordinate-{Guid.NewGuid():N}";
        ReadOnlyMemory<byte> proof = "local-version-pool"u8.ToArray();
        var calls = new ObserveCallConnection();
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = SharpLinkServerBuilder.Create().UseServiceProvider(provider)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(proof.Span)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("version-pool"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication().AddInterceptor(calls)
            .ReplaceService<IVersionInventoryProbe>(new VersionInventoryProbe());
        if (memory is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memory);

        var endpoint = new SharpLinkEndpoint
        {
            Id = endpointId,
            Address = memory is null
                ? new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port)
                : new SharpLinkSharedMemoryAddress(memory)
        };
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout()
            .UseConnectionPool(pool => { pool.MinConnections = 2; pool.MaxConnections = 2; })
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult(proof)))
            .UseEndpoint(endpoint, static entry => entry.Address switch
            {
                SharpLinkTcpAddress tcp => new SocketClientTransportFactory(new IPEndPoint(IPAddress.Parse(tcp.Host), tcp.Port)),
                SharpLinkSharedMemoryAddress shared => new SharedMemoryClientTransportFactory(shared.Name),
                _ => throw new InvalidOperationException("Unexpected pool transport.")
            });

        using var contractLoader = new PluginContext("pool-contract", pluginDirectory);
        using var localNextLoader = replaceLocal ? new PluginContext("pool-local-r2", pluginDirectory) : null;
        var contractFile = Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Contracts.dll");
        var contract = contractLoader.LoadFromAssemblyPath(contractFile);
        using var oldLoader = new PluginContext("pool-r1", pluginDirectory, contract);
        using var nextLoader = new PluginContext("pool-r2", pluginDirectory, contract);
        var serviceFile = Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Services.dll");
        var original = oldLoader.LoadFromAssemblyPath(serviceFile);
        var replacement = nextLoader.LoadFromAssemblyPath(serviceFile);
        var type = contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
        var originalService = original.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
        var newService = replacement.GetType(originalService.FullName!, throwOnError: true)!;

        await using var server = builder.Build();
        await using var client = clientBuilder.Build();
        server.SetVersionCoordinationMode(mode);
        client.SetVersionCoordinationMode(mode);
        RequireRegistration(server.RegisterAssembly(contract), "server contract");
        RequireRegistration(server.RegisterAssembly(original), "server R1");
        RequireRegistration(client.RegisterAssembly(contract), "client contract");
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        while (client.GetReadinessSnapshot().ReadyConnections != 2)
            await Task.Delay(20, ct);
        Check(client.GetVersionConnections() is { Count: 2 } initial &&
              initial.All(s => s.EndpointId == endpointId && s.ObservedSession is null),
            "listing has two independent cold selectors for the same configured endpoint");

        var factory = typeof(CoordinationSmoke).GetMethod(nameof(PreparePlugin), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type);
        var pinned = (PluginProbe)factory.Invoke(null, [client, null])!;
        var balanced = (PluginProbe)factory.Invoke(null, [client, new RpcProxyOptions { BalancePreparedEndpoints = true }])!;
        var sibling = client.CreateProxy<IVersionInventoryProbe>();
        var timeout = TimeSpan.FromSeconds(5);
        await pinned.Prepare(timeout, ct);
        Check(await pinned.Unary(1, ct) == 2, "R1 pinned generated proxy");
        var pinnedConnectionId = calls.Last;
        Check(pinnedConnectionId is not null, "record the actual original pinned transport");

        var selectors = client.GetVersionConnections();
        var pinnedSelector = selectors.Single(s => s.ObservedSession.HasValue);
        var otherSelector = selectors.Single(s => !s.ObservedSession.HasValue);
        var pinnedInventory = await client.QueryPeerVersionInventoryAsync(pinnedSelector, ct);
        var otherInventory = await client.QueryPeerVersionInventoryAsync(otherSelector, ct);
        var pinnedSession = pinnedInventory.Session!.Value;
        var otherSession = otherInventory.Session!.Value;
        Check(pinnedSession == pinnedSelector.ObservedSession && pinnedSession.PhysicalSessionId != otherSession.PhysicalSessionId &&
              pinnedSession.ServerInstanceId == otherSession.ServerInstanceId &&
              pinnedSession.ServerRegistryEpoch == otherSession.ServerRegistryEpoch &&
              pinnedInventory.PublisherEpoch == otherInventory.PublisherEpoch && pinnedInventory.Revision == otherInventory.Revision &&
              pinnedInventory.Registrations.SequenceEqual(otherInventory.Registrations) &&
              server.GetVersionPeers().Contains(pinnedSession) && server.GetVersionPeers().Contains(otherSession),
            "explicit inventory resolves each physical authority without conflating endpoint or publication");

        var declarePinned = await client.DeclareVersionTargetAsync(pinned.Local, pinnedSelector, ct);
        var declareOther = await client.DeclareVersionTargetAsync(pinned.Local, otherSelector, ct);
        var expectedStatus = deferPinned ? SharpLinkVersionDeclarationStatus.ApprovalRequired : SharpLinkVersionDeclarationStatus.Observed;
        Check(declarePinned.Status == expectedStatus && declareOther.Status == expectedStatus &&
              declarePinned.Session == pinnedSession && declareOther.Session == otherSession &&
              declarePinned.LocalRegistration == declareOther.LocalRegistration,
            "declarations remain on the selected connection while sharing the actual local registration");
        var contractId = declarePinned.LocalRegistration.ContractId;
        var r1 = pinnedInventory.Registrations.Single(r => r.ContractId == contractId);

        await balanced.Prepare(timeout, ct);
        await sibling.PrepareAsync(timeout, ct);
        Check(await sibling.Service.IncrementAsync(10, ct) == 11 && calls.Last == pinnedConnectionId,
            "the unrelated default handle has the original physical pin");

        var primary = new SharpLinkVersionParticipant(otherSession, SharpLinkVersionInitiator.Server);
        var follower = new SharpLinkVersionParticipant(pinnedSession, SharpLinkVersionInitiator.Server);
        var voted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCall = pinned.Block(ct).AsTask();
        Task<SharpLinkAssemblyReplacementResult>? localDrain = null;
        SharpLinkVersionChange served;
        Task<SharpLinkServerVersionChangeResult> serverChange;
        try
        {
            await ((Task)originalService.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
            Check(calls.Last == pinnedConnectionId, "old invocation was admitted on the pinned session");
            var localRegistrationId = declarePinned.LocalRegistration.RegistrationId;
            if (localNextLoader is not null)
            {
                var localNext = localNextLoader.LoadFromAssemblyPath(contractFile);
                var localType = localNext.GetType(type.FullName!, throwOnError: true)!;
                localDrain = client.ReplaceAssemblyAsync(contract, localNext, timeout, ct).AsTask();
                var localFactory = typeof(CoordinationSmoke)
                    .GetMethod(nameof(PreparePlugin), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(localType);
                pinned = (PluginProbe)localFactory.Invoke(null, [client, null])!;
                balanced = (PluginProbe)localFactory.Invoke(null, [client, new RpcProxyOptions { BalancePreparedEndpoints = true }])!;
                var localNextRegistration = client.GetLocalVersionInventory().Registrations.Single(r => r.ContractId == contractId);
                Check(localType != type && localNextRegistration.RegistrationId != localRegistrationId &&
                      localNextRegistration.AssemblyHash == declarePinned.LocalRegistration.AssemblyHash &&
                      !localDrain.IsCompleted && !oldCall.IsCompleted,
                    "the application really replaced the local nominal contract while the old remote call is active");
                localRegistrationId = localNextRegistration.RegistrationId;
                // Inherited physical receipts are not a data-plane grant.
                await ExpectUnavailable(() => pinned.Unary(90, ct));
                await ExpectUnavailable(() => balanced.Unary(90, ct));
            }
            var primaryChange = client.JoinVersionChangeAsync(pinned.Local, primary,
                deferPinned ? async (_, token) =>
                {
                    await voted.Task.WaitAsync(token);
                    return SharpLinkVersionDecision.Approve;
                } : null, ct).AsTask();
            var followerChange = client.JoinVersionChangeAsync(pinned.Local, follower,
                deferPinned ? (_, _) =>
                {
                    voted.TrySetResult();
                    return ValueTask.FromResult(SharpLinkVersionDecision.Defer);
                } : null, ct).AsTask();
            serverChange = server.ReplaceAssemblyCoordinatedAsync(original, replacement, contractId,
                [primary, follower], timeout, deferPinned ? static (_, _) => ValueTask.FromResult(SharpLinkVersionDecision.Approve) : null,
                ct).AsTask();
            var resumed = await Task.WhenAll(primaryChange, followerChange).WaitAsync(ct);
            served = resumed[0].Change;
            Check(resumed[0].Resumed && resumed[0].Change.Session == otherSession &&
                  resumed[1].Resumed != deferPinned && resumed[1].Change.Session == pinnedSession &&
                  resumed[0].LocalRegistrationId == localRegistrationId &&
                  resumed[1].LocalRegistrationId == (deferPinned ? (Guid?)null : localRegistrationId) &&
                  resumed[1].Change.Candidate == served.Candidate && served.Candidate.RegistrationId != r1.RegistrationId &&
                  resumed[1].DeclineReason == (deferPinned ? SharpLinkVersionDeclineReason.PolicyDeferred
                      : SharpLinkVersionDeclineReason.None) && !oldCall.IsCompleted && !serverChange.IsCompleted,
                "same client settles separate physical joins without giving the deferring session a new route");

            Check(await balanced.Unary(20, ct) == 21 && calls.Last != pinnedConnectionId &&
                  Count(newService, "Created") == 1 && Count(originalService, "Disposed") == 0,
                "balanced traffic uses the approved alternate route while the original pinned invocation is active");
            if (deferPinned)
                await ExpectUnavailable(() => pinned.Unary(21, ct));
            else
                Check(await pinned.Unary(21, ct) == 22 && calls.Last == pinnedConnectionId,
                    "approved resume preserves the default physical pin");
            Check(await sibling.Service.IncrementAsync(22, ct) == 23 && calls.Last == pinnedConnectionId,
                "unrelated traffic on the original physical connection continues");
        }
        finally
        {
            originalService.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }

        Check(await oldCall.WaitAsync(ct) == 42, "old admitted operation completes on R1");
        if (localDrain is not null)
        {
            var completed = await localDrain.WaitAsync(ct);
            Check(completed.Succeeded && completed.ReferencesReleased && completed.RemainingCalls == 0,
                "the original local registration retires only after its admitted call");
        }
        var published = await serverChange.WaitAsync(ct);
        Check(published.Replacement.Succeeded && published.Replacement.ReferencesReleased &&
              Count(originalService, "Disposed") == 1 && published.Peers.Count == 2 &&
              published.Peers[0].Session == otherSession && published.Peers[0].ActivationSent &&
              published.Peers[1].Session == pinnedSession && published.Peers[1].ActivationSent != deferPinned &&
              server.GetLocalVersionInventory().Registrations.Contains(served.Candidate),
            "one server publication drains the original registration and reports activation per exact peer");

        await server.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = 128 * 1024 },
            SharpLinkSessionRolloutMode.RollingRefresh, ct);
        IReadOnlyList<SharpLinkVersionConnection> current;
        do
        {
            current = client.GetVersionConnections();
            if (current.Count == 2 && current.All(s => s.ObservedSession is null))
                break;
            await Task.Delay(20, ct);
        }
        while (true);
        await ExpectUnavailable(() => client.QueryPeerVersionInventoryAsync(pinnedSelector, ct));
        await ExpectUnavailable(() => client.QueryPeerVersionInventoryAsync(otherSelector, ct));
        await ExpectUnavailable(() => client.JoinVersionChangeAsync(pinned.Local, primary, cancellationToken: ct));
        var fresh = await Task.WhenAll(current.Select(s => client.QueryPeerVersionInventoryAsync(s, ct).AsTask()));
        Check(current.All(s => s.EndpointId == endpointId) && fresh[0].Session != fresh[1].Session &&
              fresh.All(x => x.Session is { } peer && peer != pinnedSession && peer != otherSession &&
                  peer.ServerInstanceId == pinnedSession.ServerInstanceId &&
                  peer.ServerRegistryEpoch == pinnedSession.ServerRegistryEpoch && x.Registrations.Contains(served.Candidate)),
            "fresh selectors observe the same registry through distinct new physical authorities");
        await ExpectUnavailable(() => balanced.Unary(30, ct));
        await balanced.Prepare(timeout, ct);
        Check(await balanced.Unary(30, ct) == 31, "fresh physical routes require explicit Prepare before business traffic");
        Console.WriteLine($"VERSION_MULTI_READY_PASS transport={transport} scenario={scenario} exact=true original-drained=true refreshed=true");
    }

    private static async Task ExpectUnavailable<T>(Func<ValueTask<T>> action)
    {
        try
        {
            _ = await action();
            throw new InvalidOperationException("A retired or unapproved route was used.");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
    }

    private sealed class ObserveCallConnection : ISharpLinkServerInterceptor
    {
        private string? _last;
        internal string? Last => Volatile.Read(ref _last);

        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Volatile.Write(ref _last, context.ConnectionId);
            return next(context);
        }
    }
}
