namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyLateApplicationApprovalDoesNotApproveTheRefreshedSession(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyLateApplicationApprovalAfterRefreshAsync);

    private static async Task VerifyLateApplicationApprovalAfterRefreshAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, newType, first, _, _) = fixture;
        var (client, originalSession, originalPeer, originalA, originalB, originalReceiver, _) = first;
        var local = client.GetLocalContract(contracts.ContractAssembly, originalA.Contract);
        var originalInventory = server.GetLocalVersionInventory();
        var originalParticipant = new SharpLinkVersionParticipant(
            server.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var approvals = originalSession.RuntimeContext.NStudyApprovals;
        var oldEntered = new TaskCompletionSource<(SharpLinkVersionChange Change, CancellationToken Cancellation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAnswer = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentEntered = new TaskCompletionSource<SharpLinkVersionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentAnswer = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bound = TimeSpan.FromSeconds(5);

        // A real application decision can finish after its request to cancel.
        async ValueTask<SharpLinkVersionDecision> OriginalApproval(SharpLinkVersionChange change, CancellationToken token)
        {
            oldEntered.SetResult((change, token));
            return await oldAnswer.Task;
        }

        async ValueTask<SharpLinkVersionDecision> CurrentApproval(SharpLinkVersionChange change, CancellationToken token)
        {
            currentEntered.SetResult(change);
            return await currentAnswer.Task.WaitAsync(token);
        }

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        Ensure(await InvokeValueTaskAsync<int>(originalReceiver.Service, contracts.ContractType, "UnaryAsync", 1, ct) == 2,
            "R1 works on the original physical connection");
        var oldJoin = client.JoinVersionChangeAsync(local, originalParticipant, OriginalApproval, ct).AsTask();
        var oldReplacement = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, originalA.Contract,
            [originalParticipant], bound, Approve, ct).AsTask();
        try
        {
            var (abandoned, oldToken) = await oldEntered.Task.WaitAsync(ct);
            await server.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = 128 * 1024 },
                SharpLinkSessionRolloutMode.RollingRefresh, ct);
            await WaitForNStudyAsync(() => client.ReadyConnectionCount == 1 && !originalSession.IsConnected &&
                !ReferenceEquals(originalSession, NStudyClientSession(client)) &&
                NStudyServerRegistry(server).SnapshotActive().All(peer => !ReferenceEquals(originalPeer, peer)), ct);

            try
            {
                await oldJoin.WaitAsync(ct);
                throw new InvalidOperationException("The old operation must end with the retired connection.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable &&
                                                   originalSession.LifetimeToken.IsCancellationRequested &&
                                                   !ct.IsCancellationRequested)
            { }

            var refused = await oldReplacement.WaitAsync(ct);
            await WaitForNStudyAsync(() => oldToken.IsCancellationRequested, ct);
            Ensure(!refused.Replacement.Succeeded && refused.Peers.Single() is
            { ActivationSent: false, PreparedClientRegistrationId: null } rejected &&
                   rejected.Change == abandoned && approvals.ActiveCount == 1 &&
                   SamePrecommitInventory(originalInventory, server.GetLocalVersionInventory()),
                "the old operation ends on the retired connection while the application still owns its callback");

            var freshSession = NStudyClientSession(client);
            var freshIdentity = server.GetVersionPeers().Single();
            Ensure(freshIdentity.PhysicalSessionId != originalParticipant.Session.PhysicalSessionId &&
                   freshSession.NStudyPreparedCount == 0 && freshSession.NStudyBindOperationForDiagnostics == 0,
                "the replacement connection inherits no binding or approval");
            var (sibling, freshB) = await PrepareNStudySiblingAsync(client, originalB.Contract, ct);
            var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
            var freshA = await receiver.PrepareExact(
                new(freshB.Authority, originalA.Contract, originalA.Hash, originalA.Target), bound, ct);
            var freshParticipant = new SharpLinkVersionParticipant(freshIdentity, SharpLinkVersionInitiator.Server);
            var join = client.JoinVersionChangeAsync(local, freshParticipant, CurrentApproval, ct).AsTask();
            var replace = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, originalA.Contract,
                [freshParticipant], bound, Approve, ct).AsTask();
            var current = await currentEntered.Task.WaitAsync(ct);
            Ensure(current.Session == freshIdentity && current.TransitionId == abandoned.TransitionId &&
                   current.Candidate != abandoned.Candidate && approvals.ActiveCount == 2,
                "two application decisions are physically alive with the same number on different connections");

            oldAnswer.SetResult(SharpLinkVersionDecision.Approve);
            await WaitForNStudyAsync(() => approvals.ActiveCount == 1, ct);
            Ensure(!join.IsCompleted && !replace.IsCompleted &&
                   SamePrecommitInventory(originalInventory, server.GetLocalVersionInventory()) &&
                   freshSession.CaptureNStudyBinding(originalA.Contract) == freshA.BindingId &&
                   freshSession.CaptureNStudyBinding(originalB.Contract) == freshB.BindingId &&
                   freshSession.NStudyBindOperationForDiagnostics == 2 && NStudyCount(newType, "Created") == 0,
                "a late application approval releases only its old slot; it does not complete the new change");
            trace.Record("LateApplicationApprovalLeavesRefreshedAttemptPending", freshA, freshB);

            currentAnswer.SetResult(SharpLinkVersionDecision.Approve);
            var resumed = await join.WaitAsync(ct);
            var committed = await replace.WaitAsync(ct);
            EnsureReplacementReleased(committed.Replacement, "the independently approved fresh attempt publishes");
            var active = freshSession.CaptureNStudyRoute(originalA.Contract).Acknowledgement;
            Ensure(resumed.Resumed && resumed.Change == current && committed.Peers.Single().ActivationSent &&
                   active.Target.Registration == current.Candidate.RegistrationId &&
                   freshSession.NStudyBindOperationForDiagnostics == 3 &&
                   freshSession.CaptureNStudyBinding(originalB.Contract) == freshB.BindingId &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 2, ct) == 3 &&
                   await sibling.TouchAsync(2, ct) == 102 &&
                   ReferenceEquals(NStudyClientSession(client), freshSession),
                "only the fresh application's decision publishes R2; both routes work on the refreshed connection");
            trace.Record("OnlyFreshApplicationApprovalCanPublishAfterRefresh", active, freshB);
        }
        finally
        {
            oldAnswer.TrySetResult(SharpLinkVersionDecision.Reject);
            currentAnswer.TrySetResult(SharpLinkVersionDecision.Reject);
        }
    }
}
