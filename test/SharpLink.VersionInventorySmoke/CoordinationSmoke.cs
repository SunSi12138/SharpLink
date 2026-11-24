using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Channels;
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
        var deferFollower = name == "follower-defer";
        var manual = name == "manual" || deferFollower;
        var cohort = name == "cohort" || deferFollower;
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
        var clientNotices = new NoticeLog();
        var serverNotices = new NoticeLog();
        var followerNotices = deferFollower ? new NoticeLog() : null;
        client.ObserveVersionChanges(clientNotices.ObserveAsync);
        server.ObserveVersionChanges(serverNotices.ObserveAsync);
        if (followerNotices is not null)
            follower!.ObserveVersionChanges(followerNotices.ObserveAsync);
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
            Check(followerDeclaration.Status == (manual ? SharpLinkVersionDeclarationStatus.ApprovalRequired
                      : SharpLinkVersionDeclarationStatus.Observed) &&
                  followerDeclaration.ObservedServerRegistration == (manual ? (SharpLinkVersionRegistration?)null : r1) &&
                  followerDeclaration.LocalRegistration.RegistrationId != local.RegistrationId &&
                  followerDeclaration.Session.PhysicalSessionId != participant.Session.PhysicalSessionId &&
                  followerDeclaration.Session.ServerRegistryEpoch == participant.Session.ServerRegistryEpoch &&
                  server.GetVersionPeers().Contains(followerDeclaration.Session) &&
                  await followerPlugin.Unary(12, ct) == 13, "the follower owns its independent prepared session and publisher");
            participants = [participant, new(followerDeclaration.Session, initiator)];
        }

        int serverVotes = 0, clientVotes = 0;
        var followerDeferred = deferFollower ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        async ValueTask<SharpLinkVersionDecision> ApproveServer(SharpLinkVersionChange change, CancellationToken token)
        {
            Interlocked.Increment(ref serverVotes);
            var primary = change.Session == participant.Session;
            Check(change.Current == r1 && (primary || change.Session == followerDeclaration.Session) && change.Initiator == initiator,
                "server approval receives the exact proposal");
            var peer = await server.QueryPeerVersionInventoryAsync(change.Session, token);
            Check(peer.Registrations.Contains(primary ? local : followerDeclaration.LocalRegistration),
                "server policy can read the exact peer without blocking its session");
            return SharpLinkVersionDecision.Approve;
        }
        async ValueTask<SharpLinkVersionDecision> ApproveClient(SharpLinkVersionChange change, CancellationToken token)
        {
            Interlocked.Increment(ref clientVotes);
            Check(change.Current == r1 && change.Session == participant.Session && change.Initiator == initiator,
                "client approval receives the same exact proposal");
            Check(await sibling.Service.IncrementAsync(4, token) == 5, "client policy can await unrelated RPC");
            if (followerDeferred is not null)
                await followerDeferred.Task.WaitAsync(token);
            return SharpLinkVersionDecision.Approve;
        }

        var deferredChanges = new List<SharpLinkVersionChange>(2);
        if (manual && !deferFollower)
        {
            var decline = client.JoinVersionChangeAsync(plugin.Local, participant,
                static (_, _) => ValueTask.FromResult(SharpLinkVersionDecision.Reject), ct).AsTask();
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

            var beforeDeferral = server.GetLocalVersionInventory();
            foreach (var deferOnServer in new[] { true, false })
            {
                var deferredClient = client.JoinVersionChangeAsync(plugin.Local, participant,
                    deferOnServer ? ApproveClient : static (_, _) => ValueTask.FromResult(SharpLinkVersionDecision.Defer), ct).AsTask();
                var deferredServer = await server.ReplaceAssemblyCoordinatedAsync(original, replacement, contractId,
                    [participant], timeout, deferOnServer ? DeferServer : ApproveServer, ct);
                var clientOutcome = await deferredClient.WaitAsync(ct);
                var peerOutcome = deferredServer.Peers.Single();
                Check(!clientOutcome.Resumed && clientOutcome.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred &&
                      !deferredServer.Replacement.Succeeded && !peerOutcome.ActivationSent && peerOutcome.Change == clientOutcome.Change &&
                      peerOutcome.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred &&
                      clientOutcome.Change.TransitionId > clientRefusal.Change.TransitionId,
                    "either endpoint can defer without local publication or remote activation");
                var settled = await Task.WhenAll(clientNotices.WaitForAsync(clientOutcome.Change, SharpLinkVersionChangeStage.Deferred, ct),
                    serverNotices.WaitForAsync(clientOutcome.Change, SharpLinkVersionChangeStage.Deferred, ct));
                Check(settled.All(n => !n.LocalPublicationOccurred && n.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred),
                    "both observers distinguish deferral from rejection");
                var afterDeferral = await client.QueryPeerVersionInventoryAsync(ct);
                Check(afterDeferral.Session == participant.Session && afterDeferral.PublisherEpoch == beforeDeferral.PublisherEpoch &&
                      afterDeferral.Revision == beforeDeferral.Revision && afterDeferral.Registrations.SequenceEqual(beforeDeferral.Registrations) &&
                      Count(replacementType, "Created") == 0 && await plugin.Unary(7, ct) == 8 &&
                      await sibling.Service.IncrementAsync(40, ct) == 41,
                    "deferral leaves the exact R1 inventory and ordinary RPCs on the same session");
                deferredChanges.Add(clientOutcome.Change);
            }
            Check(deferredChanges[1].TransitionId > deferredChanges[0].TransitionId &&
                  deferredChanges[1].Candidate != deferredChanges[0].Candidate,
                "a subsequent deferral does not reuse the first staged candidate");
        }

        async ValueTask<SharpLinkVersionDecision> DeferServer(SharpLinkVersionChange change, CancellationToken token)
        {
            _ = await ApproveServer(change, token);
            return SharpLinkVersionDecision.Defer;
        }

        ValueTask<SharpLinkVersionDecision> DeferFollower(SharpLinkVersionChange change, CancellationToken token)
        {
            Check(change.Session == followerDeclaration.Session && change.Current == r1 && change.Initiator == initiator,
                "follower receives its own proposal on the distinct authenticated session");
            followerDeferred!.TrySetResult();
            return ValueTask.FromResult(SharpLinkVersionDecision.Defer);
        }

        var oldCall = plugin.Block(ct).AsTask();
        var release = originalType.GetMethod("ReleaseBlock")!;
        Task<SharpLinkServerVersionChangeResult> serverAttempt;
        SharpLinkVersionChange resumedChange;
        SharpLinkClientVersionChangeResult? followerOutcome = null;
        try
        {
            await ((Task)originalType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
            var join = client.JoinVersionChangeAsync(plugin.Local, participant, manual ? ApproveClient : null, ct).AsTask();
            var followerJoin = followerPlugin is null ? null
                : follower!.JoinVersionChangeAsync(followerPlugin.Local, participants[1], deferFollower ? DeferFollower : null, ct).AsTask();
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
            Check(deferredChanges.All(change => resumedChange.TransitionId > change.TransitionId &&
                  resumedChange.Candidate != change.Candidate),
                "approval uses a fresh proposal and cannot reuse a deferred candidate");
            var serverInventory = await client.QueryPeerVersionInventoryAsync(ct);
            Check(serverInventory.Session == participant.Session &&
                  serverInventory.Registrations.Contains(resumedChange.Candidate) &&
                  await plugin.Unary(9, ct) == 10 && await sibling.Service.IncrementAsync(19, ct) == 20 &&
                  Count(replacementType, "Created") == 1 && Count(originalType, "Disposed") == 0,
                "the same prepared proxy reaches R2 without another Prepare; B and the old call keep their owners");
            if (followerJoin is not null)
            {
                var peerOutcome = await followerJoin.WaitAsync(ct);
                followerOutcome = peerOutcome;
                Check(peerOutcome.Change.Candidate == resumedChange.Candidate &&
                      peerOutcome.Change.Session == followerDeclaration.Session && !oldCall.IsCompleted,
                    "both peers observed the same staged replacement on distinct sessions");
                if (deferFollower)
                    Check(!peerOutcome.Resumed && peerOutcome.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred,
                        "the follower's deferral terminates only its own participation");
                else
                    Check(peerOutcome.Resumed &&
                          peerOutcome.LocalRegistrationId == followerDeclaration.LocalRegistration.RegistrationId &&
                          await followerPlugin!.Unary(13, ct) == 14, "the follower resumed on its original generated proxy");
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
        if (followerOutcome is { } expectedFollower)
        {
            var followerResult = report.Peers[1];
            Check(followerResult.Session == participants[1].Session && followerResult.Change == expectedFollower.Change &&
                  followerResult.ActivationSent == expectedFollower.Resumed &&
                  followerResult.DeclineReason == expectedFollower.DeclineReason &&
                  followerResult.PreparedClientRegistrationId == expectedFollower.LocalRegistrationId,
                "server preserves explicit primary/follower order and verifies each client's own registration");
        }
        var notices = await Task.WhenAll(clientNotices.WaitForAsync(resumedChange, SharpLinkVersionChangeStage.Completed, ct),
            serverNotices.WaitForAsync(resumedChange, SharpLinkVersionChangeStage.Completed, ct));
        Check(notices.All(notice => notice.Change == resumedChange && notice.LocalPublicationOccurred &&
              notice.Stage == SharpLinkVersionChangeStage.Completed),
            "both public observers report the completed local publication of the same exact proposal");
        if (deferFollower)
        {
            var change = followerOutcome!.Value.Change;
            var noticeOnServer = await serverNotices.WaitForAsync(change, SharpLinkVersionChangeStage.Deferred, ct);
            var noticeOnFollower = await followerNotices!.WaitForAsync(change, SharpLinkVersionChangeStage.Deferred, ct);
            Check(noticeOnServer.LocalPublicationOccurred && !noticeOnFollower.LocalPublicationOccurred &&
                  noticeOnServer.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred &&
                  noticeOnFollower.DeclineReason == SharpLinkVersionDeclineReason.PolicyDeferred,
                "the server published for its primary but the deferring follower never resumed");
            Console.WriteLine($"VERSION_FOLLOWER_DEFERRAL_PASS transport={transport} primary=true follower-resumed=false");
        }
        Check(serverVotes == (deferFollower ? 2 : manual ? 4 : 0) && clientVotes == (manual ? 1 : 0),
            "every manual attempt uses its own decision");
        if (manual && !deferFollower)
            Console.WriteLine($"VERSION_DEFERRAL_PASS transport={transport} distinct=true client=true server=true fresh-retry=true");
        Console.WriteLine($"VERSION_COORDINATION_PASS transport={transport} mode={name} exact=true original-drained=true");
    }

    private sealed class NoticeLog
    {
        private readonly Channel<SharpLinkVersionChangeNotice> _events =
            Channel.CreateUnbounded<SharpLinkVersionChangeNotice>(new() { SingleReader = true, SingleWriter = true });

        internal ValueTask ObserveAsync(SharpLinkVersionChangeNotice notice, CancellationToken ct)
        {
            _events.Writer.TryWrite(notice);
            return ValueTask.CompletedTask;
        }

        internal async Task<SharpLinkVersionChangeNotice> WaitForAsync(
            SharpLinkVersionChange change, SharpLinkVersionChangeStage stage, CancellationToken ct)
        {
            while (true)
            {
                var notice = await _events.Reader.ReadAsync(ct);
                if (notice.Change == change && notice.Stage == stage)
                    return notice;
            }
        }
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
