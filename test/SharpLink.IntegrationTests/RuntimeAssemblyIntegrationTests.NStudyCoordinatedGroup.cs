namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCoordinatedCutIncludesEveryContractInItsAssemblyGroup(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyCoordinatedGroupAsync);

    private static async Task VerifyCoordinatedGroupAsync(NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, _, first, _, createClient) = fixture;
        var (client, session, peer, a, b, receiver, sibling) = first;
        ISharpLinkServer publicServer = server;
        ISharpLinkClient lead = client;
        await using var follower = createClient();
        ISharpLinkClient followerClient = follower;
        Ensure(followerClient.RegisterAssembly(contracts.ContractAssembly).Succeeded, "the second peer installs the same local contract");
        await followerClient.ConnectAsync(ct);
        var followerSession = NStudyClientSession(follower);
        var (followerSibling, followerB) = await PrepareNStudySiblingAsync(follower, b.Contract, ct);
        var cType = contracts.GetContractType("SharpLink.DynamicPlugin.IFirstThrowingDisposalService");
        var oldCType = oldAssembly.GetType("SharpLink.DynamicPlugin.FirstThrowingDisposalService", throwOnError: true)!;
        var leadC = CreatePreparedGeneratedProxy(lead, cType);
        var followerC = CreatePreparedGeneratedProxy(followerClient, cType);
        await leadC.PrepareAsync(ct);
        await followerC.PrepareAsync(ct);
        var c = session.CaptureNStudyRoute(leadC.ContractId).Acknowledgement;
        var secondC = followerSession.CaptureNStudyRoute(followerC.ContractId).Acknowledgement;
        var secondPeer = NStudyServerRegistry(server).SnapshotActive().Single(p => p.NStudyPhysicalIdentity == secondC.Authority.PhysicalSession);
        var participants = publicServer.GetVersionPeers();
        var leadParticipant = new SharpLinkVersionParticipant(participants.Single(p => p.PhysicalSessionId == a.Authority.PhysicalSession),
            SharpLinkVersionInitiator.Server);
        var followerParticipant = new SharpLinkVersionParticipant(participants.Single(p => p.PhysicalSessionId == secondC.Authority.PhysicalSession),
            SharpLinkVersionInitiator.Server);
        Ensure(c.Contract != a.Contract && c.Contract == secondC.Contract && c.Target == a.Target && secondC.Target == a.Target &&
               secondC.Authority.PhysicalSession != a.Authority.PhysicalSession && followerSession.NStudyPreparedCount == 2 &&
               followerSession.CaptureNStudyBinding(followerB.Contract) == followerB.BindingId &&
               !followerSession.HasNStudyBinding(a.Contract, a.BindingId) &&
               await InvokeValueTaskAsync<int>(leadC.Service, cType, "TouchAsync", 1, ct) == 11 &&
               await InvokeValueTaskAsync<int>(followerC.Service, cType, "TouchAsync", 2, ct) == 12,
            "both peers use C in A's assembly, but only the lead has prepared A");

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);
        var time = TimeSpan.FromSeconds(10);
        var incomplete = await publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [leadParticipant], time, Approve, ct);
        Ensure(!incomplete.Replacement.Succeeded && incomplete.Peers.Single().Change is null &&
               server.ReadNStudyTarget(oldAssembly, a.Contract) == a.Target &&
               secondPeer.NStudyBindings.HasOpenTarget(a.Target) &&
               await InvokeValueTaskAsync<int>(followerC.Service, cType, "TouchAsync", 3, ct) == 13,
            "an active peer that holds only C still belongs to the old group and cannot be omitted");
        trace.Record("AGroupCannotExcludeAPeerThatPreparedOnlyCompanionC", a, secondC);

        var followerA = CreatePreparedGeneratedProxy(followerClient, contracts.ContractType);
        await followerA.PrepareAsync(ct);
        var oldCall = InvokeValueTaskAsync<int>(followerA.Service, contracts.ContractType, "BlockAsync", ct).AsTask();
        var deciding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approveFollower = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<SharpLinkVersionDecision> HoldDecision(SharpLinkVersionChange _, CancellationToken token)
        {
            deciding.TrySetResult();
            await approveFollower.Task.WaitAsync(token);
            return SharpLinkVersionDecision.Approve;
        }

        try
        {
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
            var leadJoin = lead.JoinVersionChangeAsync(lead.GetLocalContract(contracts.ContractAssembly, a.Contract),
                leadParticipant, Approve, ct).AsTask();
            var followerJoin = followerClient.JoinVersionChangeAsync(followerClient.GetLocalContract(contracts.ContractAssembly, a.Contract),
                followerParticipant, HoldDecision, ct).AsTask();
            var replace = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
                [leadParticipant, followerParticipant], time, Approve, ct).AsTask();
            await deciding.Task.WaitAsync(ct);
            Ensure((await leadJoin.WaitAsync(ct)).Resumed && !followerJoin.IsCompleted && !oldCall.IsCompleted && !replace.IsCompleted,
                "the lead resumes before either the follower approval or its original physical call ends");
            var r2 = session.CaptureNStudyRoute(a.Contract).Acknowledgement;
            Ensure(r2.Target != a.Target && !peer.NStudyBindings.HasOpenTarget(a.Target) &&
                   !secondPeer.NStudyBindings.HasOpenTarget(a.Target) &&
                   !peer.NStudyBindings.TryResolve(c.BindingId, out _) && !secondPeer.NStudyBindings.TryResolve(secondC.BindingId, out _) &&
                   NStudyCount(oldCType, "Disposed") == 0 &&
                   await sibling.TouchAsync(1, ct) == 101 && await followerSibling.TouchAsync(1, ct) == 101,
                "the publication cuts both old C bindings without cancelling the admitted A or changing B on either peer");
            trace.Record("GroupCutClosesBothOldCompanionsBeforeFollowerApproves", c, secondC);

            approveFollower.TrySetResult();
            Ensure((await followerJoin.WaitAsync(ct)).Resumed, "the follower completes its own A resume");
            await leadC.PrepareAsync(ct);
            await followerC.PrepareAsync(ct);
            Ensure(session.CaptureNStudyRoute(c.Contract).Target == r2.Target &&
                   followerSession.CaptureNStudyRoute(c.Contract).Target == r2.Target &&
                   await InvokeValueTaskAsync<int>(leadC.Service, cType, "TouchAsync", 4, ct) == 14 &&
                   await InvokeValueTaskAsync<int>(followerC.Service, cType, "TouchAsync", 5, ct) == 15 && !oldCall.IsCompleted,
                "companion C requires fresh grants for the one R2 group and can run while the original A is held");
            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await oldCall.WaitAsync(ct) == 42, "the previously admitted A finishes in the old group");
            EnsureReplacementReleased((await replace.WaitAsync(ct)).Replacement, "old whole group drains once");
            Ensure(NStudyCount(oldType, "Disposed") == 1 && NStudyCount(oldCType, "Disposed") == 1 &&
                   session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
                   followerSession.CaptureNStudyBinding(followerB.Contract) == followerB.BindingId,
                "both old services retire once with no B rebind");
            trace.Record("NewGroupServesBothContractsWhileOriginalGroupDrainsOnce", r2, b);
        }
        finally
        {
            approveFollower.TrySetResult();
            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }
    }
}
