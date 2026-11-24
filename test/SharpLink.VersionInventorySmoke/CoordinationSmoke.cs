using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

internal static class CoordinationSmoke
{
    internal static async Task RunAsync(string transport, string name, string pluginDirectory)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var manual = name == "manual";
        var cohort = name == "cohort";
        var mode = manual ? SharpLinkVersionCoordinationMode.Manual : SharpLinkVersionCoordinationMode.Automatic;
        var initiator = manual ? SharpLinkVersionInitiator.Client : SharpLinkVersionInitiator.Server;
        var memory = transport == "tcp" ? null : $"sl-coordinate-{Guid.NewGuid():N}";
        ReadOnlyMemory<byte> proof = "local-version-smoke"u8.ToArray();
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var serverBuilder = SharpLinkServerBuilder.Create().UseServiceProvider(provider)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(proof.Span)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("version-smoke"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication().ReplaceService<IVersionInventoryProbe>(new VersionInventoryProbe());
        if (memory is null)
            serverBuilder.UseTcp(0, IPAddress.Loopback);
        else
            serverBuilder.UseSharedMemory(memory);
        var port = memory is null ? ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port : 0;
        ISharpLinkClient CreateClient()
        {
            var builder = SharpClientBuilder.Create().UseRequestTimeout()
                .UseConnectionPool(pool => { pool.MinConnections = 1; pool.MaxConnections = 1; })
                .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult(proof)));
            return (memory is null
                ? builder.UseTcp(IPAddress.Loopback.ToString(), port)
                : builder.UseSharedMemory(memory)).Build();
        }

        using var contractLoader = new PluginContext("coordination-contract", pluginDirectory);
        var contract = contractLoader.LoadFromAssemblyPath(Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Contracts.dll"));
        using var oldLoader = new PluginContext("coordination-r1", pluginDirectory, contract);
        using var newLoader = new PluginContext("coordination-r2", pluginDirectory, contract);
        var serviceFile = Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Services.dll");
        var original = oldLoader.LoadFromAssemblyPath(serviceFile);
        var replacement = newLoader.LoadFromAssemblyPath(serviceFile);
        var contractType = contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
        var originalType = original.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
        var replacementType = replacement.GetType(originalType.FullName!, throwOnError: true)!;

        await using var server = serverBuilder.Build();
        await using var client = CreateClient();
        await using var follower = cohort ? CreateClient() : null;
        server.SetVersionCoordinationMode(mode);
        client.SetVersionCoordinationMode(mode);
        follower?.SetVersionCoordinationMode(mode);
        var clientCompleted = ObserveCompletion(client.ObserveVersionChanges);
        var serverCompleted = ObserveCompletion(server.ObserveVersionChanges);
        RequireRegistration(server.RegisterAssembly(contract), "server contract");
        RequireRegistration(server.RegisterAssembly(original), "server R1");
        RequireRegistration(client.RegisterAssembly(contract), "client contract");
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);

        // The plugin type is application-loaded; only public generated proxy APIs are called.
        var factory = typeof(CoordinationSmoke).GetMethod(nameof(PreparePlugin), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(contractType);
        var plugin = (PluginProbe)factory.Invoke(null, [client])!;
        var sibling = client.CreateProxy<IVersionInventoryProbe>();
        var timeout = TimeSpan.FromSeconds(5);
        await plugin.Prepare(timeout, ct);
        await sibling.PrepareAsync(timeout, ct);

        var declaration = await client.DeclareVersionTargetAsync(plugin.Local, ct);
        var participant = new SharpLinkVersionParticipant(declaration.Session, initiator);
        var contractId = declaration.LocalRegistration.ContractId;
        var local = client.GetLocalVersionInventory().Registrations.Single(x => x.ContractId == contractId);
        var r1 = server.GetLocalVersionInventory().Registrations.Single(x => x.ContractId == contractId);
        Check(local == declaration.LocalRegistration && r1.AssemblyHash == local.AssemblyHash &&
              declaration.Status == (manual ? SharpLinkVersionDeclarationStatus.ApprovalRequired : SharpLinkVersionDeclarationStatus.Observed) &&
              declaration.ObservedServerRegistration == (manual ? (SharpLinkVersionRegistration?)null : r1) &&
              Count(replacementType, "Created") == 0 && await plugin.Unary(3, ct) == 4,
            "declaring an installed target does not publish the replacement or change routing");

        SharpLinkVersionParticipant[] participants = [participant];
        PluginProbe? followerPlugin = null;
        SharpLinkVersionDeclarationResult followerDeclaration = default;
        if (follower is not null)
        {
            RequireRegistration(follower.RegisterAssembly(contract), "follower client contract");
            await follower.ConnectAsync(ct);
            followerPlugin = (PluginProbe)factory.Invoke(null, [follower])!;
            await followerPlugin.Prepare(timeout, ct);
            followerDeclaration = await follower.DeclareVersionTargetAsync(followerPlugin.Local, ct);
            Check(followerDeclaration.Status == SharpLinkVersionDeclarationStatus.Observed &&
                  followerDeclaration.ObservedServerRegistration == r1 &&
                  followerDeclaration.LocalRegistration.RegistrationId != local.RegistrationId &&
                  followerDeclaration.Session.PhysicalSessionId != participant.Session.PhysicalSessionId &&
                  followerDeclaration.Session.ServerRegistryEpoch == participant.Session.ServerRegistryEpoch &&
                  server.GetVersionPeers().Contains(followerDeclaration.Session) &&
                  await followerPlugin.Unary(12, ct) == 13, "the follower owns its independent prepared session and publisher");
            participants = [participant, new(followerDeclaration.Session, initiator)];
        }

        int serverVotes = 0, clientVotes = 0;
        async ValueTask<bool> ApproveServer(SharpLinkVersionChange change, CancellationToken token)
        {
            Interlocked.Increment(ref serverVotes);
            Check(change.Current == r1 && change.Session == participant.Session && change.Initiator == initiator,
                "server approval receives the exact proposal");
            var peer = await server.QueryPeerVersionInventoryAsync(change.Session, token);
            Check(peer.Registrations.Contains(local), "server policy can read the peer without blocking its session");
            return true;
        }
        async ValueTask<bool> ApproveClient(SharpLinkVersionChange change, CancellationToken token)
        {
            Interlocked.Increment(ref clientVotes);
            Check(change.Current == r1 && change.Session == participant.Session && change.Initiator == initiator,
                "client approval receives the same exact proposal");
            Check(await sibling.Service.IncrementAsync(4, token) == 5, "client policy can await unrelated RPC");
            return true;
        }

        if (manual)
        {
            var decline = client.JoinVersionChangeAsync(plugin.Local, participant, static (_, _) => ValueTask.FromResult(false), ct).AsTask();
            var refused = await server.ReplaceAssemblyCoordinatedAsync(original, replacement, contractId,
                [participant], timeout, ApproveServer, ct);
            var clientRefusal = await decline.WaitAsync(ct);
            Check(!clientRefusal.Resumed && clientRefusal.DeclineReason == SharpLinkVersionDeclineReason.Policy &&
                  !refused.Replacement.Succeeded && !refused.Peers.Single().ActivationSent &&
                  refused.Peers.Single().Change == clientRefusal.Change &&
                  refused.Peers.Single().DeclineReason == SharpLinkVersionDeclineReason.Policy &&
                  server.GetLocalVersionInventory().Registrations.Contains(r1) &&
                  Count(replacementType, "Created") == 0 && await plugin.Unary(6, ct) == 7,
                "policy refusal leaves the original published generation reachable");
        }

        var oldCall = plugin.Block(ct).AsTask();
        var release = originalType.GetMethod("ReleaseBlock")!;
        Task<SharpLinkServerVersionChangeResult> serverAttempt;
        SharpLinkVersionChange resumedChange;
        SharpLinkClientVersionChangeResult? followerResumed = null;
        try
        {
            await ((Task)originalType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
            var join = client.JoinVersionChangeAsync(plugin.Local, participant, manual ? ApproveClient : null, ct).AsTask();
            var followerJoin = followerPlugin is null ? null
                : follower!.JoinVersionChangeAsync(followerPlugin.Local, participants[1], cancellationToken: ct).AsTask();
            serverAttempt = server.ReplaceAssemblyCoordinatedAsync(original, replacement, contractId,
                participants, timeout, manual ? ApproveServer : null, ct).AsTask();
            var resumed = await join.WaitAsync(ct);
            resumedChange = resumed.Change;
            Check(resumed.Resumed && resumed.DeclineReason == SharpLinkVersionDeclineReason.None &&
                  resumed.LocalRegistrationId == local.RegistrationId && resumedChange.Current == r1 &&
                  resumedChange.Candidate.RegistrationId != r1.RegistrationId &&
                  resumedChange.Candidate.PublicationGeneration > r1.PublicationGeneration &&
                  resumedChange.Candidate.AssemblyHash == r1.AssemblyHash && !oldCall.IsCompleted && !serverAttempt.IsCompleted,
                "client resumed to a different same-contract registration before old physical drain finished");
            var serverInventory = await client.QueryPeerVersionInventoryAsync(ct);
            Check(serverInventory.Session == participant.Session &&
                  serverInventory.Registrations.Contains(resumedChange.Candidate) &&
                  await plugin.Unary(9, ct) == 10 && await sibling.Service.IncrementAsync(19, ct) == 20 &&
                  Count(replacementType, "Created") == 1 && Count(originalType, "Disposed") == 0,
                "the same prepared proxy reaches R2 without another Prepare; B and the old call keep their owners");
            if (followerJoin is not null)
            {
                followerResumed = await followerJoin.WaitAsync(ct);
                var resumedPeer = followerResumed.Value;
                Check(resumedPeer.Resumed && resumedPeer.Change.Candidate == resumedChange.Candidate &&
                      resumedPeer.Change.Session == followerDeclaration.Session &&
                      resumedPeer.LocalRegistrationId == followerDeclaration.LocalRegistration.RegistrationId &&
                      await followerPlugin!.Unary(13, ct) == 14 && !oldCall.IsCompleted,
                    "the follower resumed to the same committed registration on its own prepared session");
            }
        }
        finally
        {
            release.Invoke(null, null);
        }
        Check(await oldCall.WaitAsync(ct) == 42, "the old invocation ends in its original handler");

        var report = await serverAttempt.WaitAsync(ct);
        Check(report.Peers.Count == participants.Length, "one result per selected participant");
        var peerResult = report.Peers[0];
        Check(report.Replacement.Succeeded && report.Replacement.ReferencesReleased &&
              report.Replacement.RemainingCalls == 0 && Count(originalType, "Disposed") == 1 &&
              peerResult.Session == participant.Session && peerResult.Change == resumedChange &&
              peerResult.ActivationSent && peerResult.PreparedClientRegistrationId == local.RegistrationId &&
              peerResult.DeclineReason == SharpLinkVersionDeclineReason.None,
            "server separately reports local publish, old drain, and Activate sent to the chosen peer");
        if (followerResumed is { } resumedFollower)
        {
            var followerResult = report.Peers[1];
            Check(followerResult.Session == participants[1].Session && followerResult.Change == resumedFollower.Change &&
                  followerResult.ActivationSent && followerResult.DeclineReason == SharpLinkVersionDeclineReason.None &&
                  followerResult.PreparedClientRegistrationId == resumedFollower.LocalRegistrationId,
                "server preserves explicit primary/follower order and verifies each client's own registration");
        }
        var notices = await Task.WhenAll(clientCompleted, serverCompleted).WaitAsync(ct);
        Check(notices.All(notice => notice.Change == resumedChange && notice.LocalPublicationOccurred &&
              notice.Stage == SharpLinkVersionChangeStage.Completed),
            "both public observers report the completed local publication of the same exact proposal");
        Check(serverVotes == (manual ? 2 : 0) && clientVotes == (manual ? 1 : 0), "application decisions run only in Manual");
        Console.WriteLine($"VERSION_COORDINATION_PASS transport={transport} mode={name} exact=true original-drained=true");
    }

    private static Task<SharpLinkVersionChangeNotice> ObserveCompletion(
        Action<Func<SharpLinkVersionChangeNotice, CancellationToken, ValueTask>> subscribe)
    {
        var observed = new TaskCompletionSource<SharpLinkVersionChangeNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe((notice, _) =>
        {
            if (notice.Stage == SharpLinkVersionChangeStage.Completed)
                observed.TrySetResult(notice);
            return ValueTask.CompletedTask;
        });
        return observed.Task;
    }

    private static PluginProbe PreparePlugin<T>(ISharpLinkClient client) where T : IService
    {
        var proxy = client.CreateProxy<T>();
        var unary = typeof(T).GetMethod("UnaryAsync")!;
        var block = typeof(T).GetMethod("BlockAsync")!;
        return new(client.GetLocalContract<T>(), proxy.PrepareAsync,
            (value, ct) => (ValueTask<int>)unary.Invoke(proxy.Service, [value, ct])!,
            ct => (ValueTask<int>)block.Invoke(proxy.Service, [ct])!);
    }

    private sealed record PluginProbe(RpcLocalContract Local, Func<TimeSpan, CancellationToken, ValueTask> Prepare,
        Func<int, CancellationToken, ValueTask<int>> Unary, Func<CancellationToken, ValueTask<int>> Block);

    private static int Count(Type plugin, string property) => (int)plugin.GetProperty(property)!.GetValue(null)!;

    private static void RequireRegistration(SharpLinkAssemblyRegistrationResult result, string stage)
        => Check(result.Succeeded, $"{stage} registered: {result.Error}");

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private sealed class PluginContext(string name, string directory, Assembly? contract = null)
        : AssemblyLoadContext(name, isCollectible: true), IDisposable
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (contract is not null && AssemblyName.ReferenceMatchesDefinition(contract.GetName(), assemblyName))
                return contract;
            var shared = Default.Assemblies.FirstOrDefault(x => AssemblyName.ReferenceMatchesDefinition(x.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            // Package dependencies present in the consumer's normal deps manifest are shared through Default.
            var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        public void Dispose() => Unload();
    }
}
