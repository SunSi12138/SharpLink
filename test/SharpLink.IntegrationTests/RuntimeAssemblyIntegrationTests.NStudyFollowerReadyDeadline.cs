using SharpLink.UnitTests.Runtime;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyFollowerWithBlockedReadyClosesAtItsControlDeadline(string transport)
    {
        var clock = new ManualTimeProvider();
        Guid followerId = default;
        var writer = new NStudyOutboundProbe(frame => frame.Control is { Phase: NStudyPhase.Ready } control &&
            control.Change.Authority.PhysicalSession == followerId);
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            (fixture, trace, ct) =>
            {
                followerId = fixture.Second!.A.Authority.PhysicalSession;
                return VerifyNStudyFollowerReadyDeadlineAsync(fixture, trace, writer, clock, ct);
            },
            decorateTransport: transportFactory => new NStudyFenceTransportFactory(transportFactory, writer),
            configureServer: builder => builder.UseTimeProvider(clock));
    }

    private static async Task VerifyNStudyFollowerReadyDeadlineAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyOutboundProbe writer, ManualTimeProvider clock, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, lead, followerOrNull, createClient) = fixture;
        var follower = followerOrNull ?? throw new InvalidOperationException("Expected two prepared peers.");
        ISharpLinkServer publicServer = server;
        ISharpLinkClient leadClient = lead.Client;
        ISharpLinkClient followerClient = follower.Client;
        var peers = publicServer.GetVersionPeers();
        SharpLinkVersionParticipant Participant(NStudyTestPeer peer) => new(peers.Single(session =>
            session.PhysicalSessionId == peer.A.Authority.PhysicalSession), SharpLinkVersionInitiator.Server);
        var leading = Participant(lead);
        var following = Participant(follower);
        var offer = new TaskCompletionSource<SharpLinkVersionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vote = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followerBinds = follower.Session.NStudyBindOperationForDiagnostics;

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        async ValueTask<SharpLinkVersionDecision> FollowerDecision(SharpLinkVersionChange change, CancellationToken token)
        {
            offer.TrySetResult(change);
            return await vote.Task.WaitAsync(token);
        }

        Ensure(await InvokeValueTaskAsync<int>(follower.Receiver.Service, contracts.ContractType, "UnaryAsync", 1, ct) == 2,
            "the follower has a working original grant before any proposal");
        var joinLead = leadClient.JoinVersionChangeAsync(leadClient.GetLocalContract(contracts.ContractAssembly, lead.A.Contract),
            leading, Approve, ct).AsTask();
        var joinFollower = followerClient.JoinVersionChangeAsync(
            followerClient.GetLocalContract(contracts.ContractAssembly, follower.A.Contract),
            following, FollowerDecision, ct).AsTask();
        var replace = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, lead.A.Contract,
            [leading, following], TimeSpan.FromSeconds(5), Approve, ct).AsTask();
        try
        {
            var proposal = await offer.Task.WaitAsync(ct);
            var joined = await joinLead.WaitAsync(ct);
            var r2 = lead.Session.CaptureNStudyRoute(lead.A.Contract).Acknowledgement;
            var committed = server.GetLocalVersionInventory();
            Ensure(joined.Resumed && committed.Registrations.Contains(joined.Change.Candidate) &&
                   proposal.Candidate == joined.Change.Candidate && proposal.Session == following.Session &&
                   !follower.ServerConnection.NStudyBindings.HasOpenTarget(follower.A.Target) &&
                   await follower.Sibling.TouchAsync(2, ct) == 102,
                "the leader has committed and resumed while the other physical peer still has only B");

            writer.StartRecording();
            vote.TrySetResult(SharpLinkVersionDecision.Approve);
            await writer.FlushEntered.Task.WaitAsync(ct);
            Ensure(writer.Flushed.All(frame => frame.Control is not { Phase: NStudyPhase.Ready }) &&
                   !joinFollower.IsCompleted && !replace.IsCompleted &&
                   !follower.ServerConnection.Session.MayBindNStudyTarget(r2.Target) &&
                   follower.Session.NStudyBindOperationForDiagnostics == followerBinds,
                "the exact follower Ready is held in a real writer and cannot authorize a Bind");
            await ExpectPrecommitUnavailableAsync(() => InvokeValueTaskAsync<int>(
                follower.Receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct));
            Ensure(NStudyCount(newType, "Created") == 0,
                "no new business handler entered before the leader's first R2 call");
            Ensure(await InvokeValueTaskAsync<int>(lead.Receiver.Service, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
                   await lead.Sibling.TouchAsync(4, ct) == 104,
                "R2 and B on the leader do not depend on the other writer");
            trace.Record("FollowerReadyCannotGrantR2WhileItsPhysicalWriterIsHeld", r2, follower.A);

            clock.Advance(NStudyApprovalScheduler.ControlTimeout);
            var result = await replace.WaitAsync(ct);
            await follower.ServerConnection.SessionTask.WaitAsync(ct);
            try
            {
                await joinFollower.WaitAsync(ct);
                throw new InvalidOperationException("The follower must not complete a Ready that missed its deadline.");
            }
            catch (SharpLinkException error) when (error.Code is SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable)
            {
                // The send pump and the receive lifetime can each be first to observe the closure.
                Console.WriteLine($"Follower control ended with {error.Code}.");
            }
            EnsureReplacementReleased(result.Replacement, "the follower deadline cannot roll back the leader's commit");
            Ensure(!ct.IsCancellationRequested && result.Peers[0] is { ActivationSent: true } leadResult &&
                   leadResult.Change == joined.Change && result.Peers[1] is
                   { ActivationSent: false, PreparedClientRegistrationId: null } followerResult &&
                   followerResult.Change == proposal && !follower.Session.IsConnected &&
                   writer.Flushed.All(frame => frame.Control is not { Phase: NStudyPhase.Ready }) &&
                   follower.Session.NStudyBindOperationForDiagnostics == followerBinds &&
                   SamePrecommitInventory(committed, server.GetLocalVersionInventory()) &&
                   ReferenceEquals(NStudyClientSession(lead.Client), lead.Session) && NStudyCount(oldType, "Disposed") == 1,
                "the real control deadline closes only the stalled connection, never resuming R2 on it");
            trace.Record("FollowerControlDeadlineEndsOnlyItsUnconfirmedPhysicalSession", r2, follower.A);

            writer.Release();
            await using var fresh = createClient();
            Ensure(fresh.RegisterAssembly(contracts.ContractAssembly).Succeeded, "the reconnecting app owns its compatible contract");
            await fresh.ConnectAsync(ct);
            var freshSession = NStudyClientSession(fresh);
            var inventory = await fresh.QueryPeerVersionInventoryAsync(ct);
            Ensure(inventory.Session is { } currentSession && currentSession.PhysicalSessionId != following.Session.PhysicalSessionId &&
                   inventory.Registrations.Contains(proposal.Candidate) &&
                   freshSession.NStudyPreparedCount == 0 && freshSession.NStudyBindOperationForDiagnostics == 0,
                "new physical authority sees the committed registry but inherits no grant");
            var (sibling, b) = await PrepareNStudySiblingAsync(fresh, follower.B.Contract, ct);
            var receiver = CreateNStudyExactGenerated(fresh, contracts.ContractType);
            var a = await receiver.PrepareExact(new(b.Authority, follower.A.Contract, follower.A.Hash, r2.Target),
                TimeSpan.FromSeconds(5), ct);
            Ensure(a.Authority == b.Authority && a.Authority != follower.A.Authority && a.Target == r2.Target &&
                   freshSession.NStudyBindOperationForDiagnostics == 2 &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 5, ct) == 6 &&
                   await sibling.TouchAsync(5, ct) == 105 && await lead.Sibling.TouchAsync(6, ct) == 106,
                "only independent preparation restores the second peer, and the lead session stays healthy");
            trace.Record("FreshPhysicalPreparationRecoversTheFollowerAfterItsDeadline", a, b);
        }
        finally
        {
            vote.TrySetResult(SharpLinkVersionDecision.Reject);
            writer.Release();
        }
    }
}
