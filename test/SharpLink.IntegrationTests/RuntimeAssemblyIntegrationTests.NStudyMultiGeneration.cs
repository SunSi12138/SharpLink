namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyLateApplicationApprovalCannotUndoTwoPublishedGenerations(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyNStudyLateApprovalAcrossGenerationsAsync);

    private static async Task VerifyNStudyLateApprovalAcrossGenerationsAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var a = receiver.Service;
        var local = client.GetLocalContract(contracts.ContractAssembly, a1.Contract);
        var participant = new SharpLinkVersionParticipant(
            server.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var approvals = session.RuntimeContext.NStudyApprovals;
        var initialBinds = session.NStudyBindOperationForDiagnostics;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        async ValueTask<SharpLinkVersionDecision> SlowApproval(SharpLinkVersionChange _, CancellationToken token)
        {
            entered.SetResult(token);
            try { return await answer.Task; }
            finally { callbackExited.TrySetResult(); }
        }
        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        try
        {
            var thirdAssembly = thirdLoader.LoadFromAssemblyPath(newAssembly.Location);
            var thirdType = thirdAssembly.GetType(newType.FullName!, throwOnError: true)!;
            Ensure(await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 1, ct) == 2,
                "actual R1 is active before the original offer");
            var staleJoin = client.JoinVersionChangeAsync(local, participant, SlowApproval, ct).AsTask();
            var staleReplace = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a1.Contract,
                [participant], TimeSpan.FromSeconds(5), Approve, ct).AsTask();
            var oldApprovalToken = await entered.Task.WaitAsync(ct);
            var abandoned = await staleJoin.WaitAsync(ct);
            var refusal = await staleReplace.WaitAsync(ct);
            await WaitForNStudyAsync(() => oldApprovalToken.IsCancellationRequested, ct);
            Ensure(!abandoned.Resumed && abandoned.DeclineReason == SharpLinkVersionDeclineReason.Policy &&
                   !refusal.Replacement.Succeeded && refusal.Peers.Single() is
                       { ActivationSent: false, PreparedClientRegistrationId: null } &&
                   refusal.Peers[0].Change == abandoned.Change &&
                   !callbackExited.Task.IsCompleted && approvals.ActiveCount == 1 &&
                   session.NStudyBindOperationForDiagnostics == initialBinds &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   NStudyCount(newType, "Created") == 0 && await b.TouchAsync(1, ct) == 101,
                "the expired original attempt leaves R1, no Bind and one real callback still running");
            trace.Record("ExpiredStageLeavesR1AndPhysicalApprovalSlot", a1, b1);

            var r2 = await PublishAsync(oldAssembly, newAssembly, a1, abandoned.Change.TransitionId, 2, 1);
            Ensure(r2.Change.Candidate != abandoned.Change.Candidate && NStudyCount(oldType, "Disposed") == 1 &&
                   NStudyCount(newType, "Created") == 1, "real R2 is not the abandoned staged registration");
            var r3 = await PublishAsync(newAssembly, thirdAssembly, r2.Binding, r2.Change.TransitionId, 3, 2);
            Ensure(NStudyCount(newType, "Disposed") == 1 && NStudyCount(thirdType, "Created") == 1 &&
                   !callbackExited.Task.IsCompleted && approvals.ActiveCount == 1 && await b.TouchAsync(2, ct) == 102,
                "both successive replacements finish while the original application callback still owns a slot");
            trace.Record("TwoLaterPublicationsRunBeforeOldApprovalReturns", r2.Binding, r3.Binding);

            answer.TrySetResult(SharpLinkVersionDecision.Approve);
            await callbackExited.Task.WaitAsync(ct);
            await WaitForNStudyAsync(() => approvals.ActiveCount == 0, ct);
            var inventory = await client.QueryPeerVersionInventoryAsync(ct);
            Ensure(inventory.Session == participant.Session &&
                   inventory.Registrations.Single(registration => registration.ContractId == a1.Contract) == r3.Change.Candidate &&
                   !peer.NStudyBindings.HasOpenTarget(a1.Target) && !peer.NStudyBindings.HasOpenTarget(r2.Binding.Target) &&
                   session.CaptureNStudyBinding(a1.Contract) == r3.Binding.BindingId &&
                   session.CaptureNStudyBinding(b1.Contract) == b1.BindingId &&
                   session.NStudyBindOperationForDiagnostics == initialBinds + 2 &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
                   await b.TouchAsync(3, ct) == 103 &&
                   NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Disposed") == 1,
                "late approval cannot change the current R3 or reopen either retired registration");
            await WaitForNStudyAsync(() => CohortPeerDrained(first, NStudyClientConnection(client), 2), ct);
            trace.Record("LateApprovalFreesOnlyItsSlotAndLeavesR3Current", r3.Binding, b1);

            async Task<(SharpLinkVersionChange Change, NStudyAck Binding)> PublishAsync(
                System.Reflection.Assembly from, System.Reflection.Assembly to, NStudyAck original,
                ulong precedingTransition, int value, ulong binds)
            {
                var joining = client.JoinVersionChangeAsync(local, participant, Approve, ct).AsTask();
                var replacing = server.ReplaceAssemblyCoordinatedAsync(from, to, a1.Contract, [participant],
                    TimeSpan.FromSeconds(5), Approve, ct).AsTask();
                var outcome = await joining.WaitAsync(ct);
                var replacement = await replacing.WaitAsync(ct);
                EnsureReplacementReleased(replacement.Replacement, "the former generation retires physically");
                var active = session.CaptureNStudyRoute(a1.Contract).Acknowledgement;
                Ensure(outcome.Resumed && outcome.Change.TransitionId > precedingTransition &&
                       outcome.Change.Current.RegistrationId == original.Target.Registration &&
                       outcome.Change.Candidate.RegistrationId == active.Target.Registration &&
                       outcome.Change.Candidate.AssemblyHash == outcome.Change.Current.AssemblyHash &&
                       outcome.Change.Candidate != outcome.Change.Current &&
                       replacement.Peers.Single() is { ActivationSent: true } activated &&
                       activated.Change == outcome.Change && activated.PreparedClientRegistrationId == outcome.LocalRegistrationId &&
                       active.Authority == original.Authority && active.Target == server.ReadNStudyTarget(to, a1.Contract) &&
                       session.NStudyBindOperationForDiagnostics == initialBinds + binds &&
                       await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", value, ct) == value + 1,
                    "ordinary fresh approvals and an exact Bind reach only the newly published generation");
                return (outcome.Change, active);
            }
        }
        finally
        {
            answer.TrySetResult(SharpLinkVersionDecision.Reject);
            thirdLoader.Unload();
        }
    }
}
