namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyPublicApprovalCanCallSiblingBeforeDecliningOrApproving(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyNStudyPublicApprovalAsync);

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyPublicApprovalCannotStartAnotherReplacementOnItsSession(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyNStudyPublicApprovalReentryAsync);

    [Test]
    [NotInParallel]
    [Arguments("tcp", true, true)]
    [Arguments("tcp", false, true)]
    [Arguments("shared-memory", true, true)]
    [Arguments("shared-memory", false, true)]
    [Arguments("tcp", true, false)]
    [Arguments("tcp", false, false)]
    [Arguments("shared-memory", true, false)]
    [Arguments("shared-memory", false, false)]
    public Task NStudyPublicApprovalFailureDoesNotRetireTheOriginalRoute(
        string transport, bool approvalOnClient, bool waitForTimeout)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) =>
                VerifyPublicApprovalFailureAsync(fixture, trace, approvalOnClient, waitForTimeout, ct));

    private static async Task VerifyPublicApprovalFailureAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, bool approvalOnClient, bool waitForTimeout,
        CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, newType, first, _, _) = fixture;
        var (client, session, serverConnection, a, b, receiver, sibling) = first;
        ISharpLinkClient publicClient = client;
        ISharpLinkServer publicServer = server;
        var participant = new SharpLinkVersionParticipant(
            publicServer.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var contract = publicClient.GetLocalContract(contracts.ContractAssembly, a.Contract);
        var published = publicServer.GetLocalVersionInventory();
        var binds = session.NStudyBindOperationForDiagnostics;
        var approvals = (approvalOnClient ? session : serverConnection.Session).RuntimeContext.NStudyApprovals;
        var entered = new TaskCompletionSource<(SharpLinkVersionChange Change, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        async ValueTask<SharpLinkVersionDecision> CheckAsync(SharpLinkVersionChange change, CancellationToken token)
        {
            entered.SetResult((change, token));
            try
            {
                await finishCheck.Task.WaitAsync(token);
                throw new InvalidOperationException("The application's approval check did not complete successfully.");
            }
            finally
            {
                exited.TrySetResult();
            }
        }

        Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 1, ct) == 2,
            "the original route is ready before the application checks the proposed deployment");
        var joining = publicClient.JoinVersionChangeAsync(contract, participant,
            approvalOnClient ? CheckAsync : Approve, ct).AsTask();
        var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [participant], TimeSpan.FromSeconds(5), approvalOnClient ? Approve : CheckAsync, ct).AsTask();
        try
        {
            var (proposal, token) = await entered.Task.WaitAsync(ct);
            Ensure(proposal.Session == participant.Session && !token.IsCancellationRequested &&
                   !joining.IsCompleted && !replacing.IsCompleted && await sibling.TouchAsync(1, ct) == 101,
                "the application is checking the expected Session while unrelated business continues");
            if (!waitForTimeout)
                finishCheck.TrySetResult();

            await Task.WhenAll(joining, replacing).WaitAsync(ct);
            await exited.Task.WaitAsync(ct);
            await WaitForNStudyAsync(() => approvals.ActiveCount == 0, ct);
            var declined = await joining;
            var rejected = await replacing;
            Ensure(token.IsCancellationRequested == waitForTimeout &&
                   !declined.Resumed && declined.DeclineReason == SharpLinkVersionDeclineReason.Policy &&
                   declined.Change == proposal && !rejected.Replacement.Succeeded &&
                   rejected.Peers.Single() is { ActivationSent: false, DeclineReason: SharpLinkVersionDeclineReason.Policy } peer &&
                   peer.Change == proposal && SamePrecommitInventory(published, publicServer.GetLocalVersionInventory()) &&
                   NStudyCount(newType, "Created") == 0 && ReferenceEquals(session, NStudyClientSession(client)) &&
                   session.CaptureNStudyBinding(a.Contract) == a.BindingId &&
                   session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
                   session.NStudyBindOperationForDiagnostics == binds &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 2, ct) == 3,
                "an expired or failed public approval settles without publishing or retiring either original route");
            trace.Record(waitForTimeout ? "PublicApprovalTimedOutWithoutRetiringR1" : "PublicApprovalFaultRetainsR1", a, b);

            var retryJoin = publicClient.JoinVersionChangeAsync(contract, participant, Approve, ct).AsTask();
            var retryReplace = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
                [participant], TimeSpan.FromSeconds(5), Approve, ct).AsTask();
            await Task.WhenAll(retryJoin, retryReplace).WaitAsync(ct);
            var resumed = await retryJoin;
            var replacement = await retryReplace;
            EnsureReplacementReleased(replacement.Replacement, "a fresh approval succeeds after the prior attempt");
            Ensure(resumed.Resumed && resumed.Change.TransitionId > proposal.TransitionId &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                   await sibling.TouchAsync(2, ct) == 102,
                "a new transition resumes the original proxy and preserves the unrelated binding");
            trace.Record("PublicApprovalRetryResumesOnlyTheChangedRoute",
                session.CaptureNStudyRoute(a.Contract).Acknowledgement, b);
        }
        finally
        {
            finishCheck.TrySetResult();
        }
    }

    private static async Task VerifyNStudyPublicApprovalReentryAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, _, a, b, receiver, sibling) = first;
        ISharpLinkServer publicServer = server;
        ISharpLinkClient publicClient = client;
        var contract = publicClient.GetLocalContract(contracts.ContractAssembly, a.Contract);
        var participant = new SharpLinkVersionParticipant(
            publicServer.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var binds = session.NStudyBindOperationForDiagnostics;
        var overlapDeclined = false;
        Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 1, ct) == 2 &&
               NStudyCount(oldType, "Created") == 1,
            "an actual call instantiates the original implementation before the operation");

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        async ValueTask<SharpLinkVersionDecision> ApproveAfterCheckingOverlap(
            SharpLinkVersionChange _, CancellationToken token)
        {
            try
            {
                await publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
                    [participant], TimeSpan.FromSeconds(5), Approve, token);
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                overlapDeclined = true;
                return SharpLinkVersionDecision.Approve;
            }
            return SharpLinkVersionDecision.Reject;
        }

        var joining = publicClient.JoinVersionChangeAsync(contract, participant, Approve, ct).AsTask();
        var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [participant], TimeSpan.FromSeconds(5), ApproveAfterCheckingOverlap, ct).AsTask();
        await Task.WhenAll(joining, replacing).WaitAsync(ct);
        var joined = await joining;
        var replaced = await replacing;
        Ensure(overlapDeclined, "the nested public call must return the busy-session error to the callback");
        EnsureReplacementReleased(replaced.Replacement, "the original approval does not wait on its own Session");
        var active = session.CaptureNStudyRoute(a.Contract).Acknowledgement;
        Ensure(joined.Resumed && replaced.Peers.Single() is { ActivationSent: true } activated &&
               activated.Change == joined.Change && active.Target == server.ReadNStudyTarget(newAssembly, a.Contract) &&
               ReferenceEquals(session, NStudyClientSession(client)) &&
               session.NStudyBindOperationForDiagnostics == binds + 1 &&
               session.CaptureNStudyBinding(b.Contract) == b.BindingId && await sibling.TouchAsync(1, ct) == 101 &&
               await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 2, ct) == 3 &&
               NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 1,
            "only the original change is published and both routes keep serving on the same Session");
        trace.Record("PublicApprovalRejectsAnOverlappingChangeBeforeItsOnlyPublication", a, active);
    }

    private static async Task VerifyNStudyPublicApprovalAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, newType, first, _, _) = fixture;
        var (client, session, _, a, b, receiver, sibling) = first;
        ISharpLinkServer publicServer = server;
        ISharpLinkClient publicClient = client;
        var contract = publicClient.GetLocalContract(contracts.ContractAssembly, a.Contract);
        var participant = new SharpLinkVersionParticipant(
            publicServer.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var binds = session.NStudyBindOperationForDiagnostics;
        var serverCallbackResult = 0;
        var clientCallbackResult = 0;

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        async ValueTask<SharpLinkVersionDecision> RejectAfterCheckingSibling(
            SharpLinkVersionChange _, CancellationToken token)
        {
            serverCallbackResult = await sibling.TouchAsync(1, token);
            return SharpLinkVersionDecision.Reject;
        }

        async ValueTask<SharpLinkVersionDecision> ApproveAfterCheckingSibling(
            SharpLinkVersionChange _, CancellationToken token)
        {
            clientCallbackResult = await sibling.TouchAsync(2, token);
            return SharpLinkVersionDecision.Approve;
        }

        var decliningClient = publicClient.JoinVersionChangeAsync(contract, participant, Approve, ct).AsTask();
        var decliningServer = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [participant], TimeSpan.FromSeconds(5), RejectAfterCheckingSibling, ct).AsTask();
        await Task.WhenAll(decliningClient, decliningServer).WaitAsync(ct);
        var refusal = await decliningServer;
        var declined = await decliningClient;
        Ensure(serverCallbackResult == 101 && !refusal.Replacement.Succeeded && !declined.Resumed &&
               declined.DeclineReason == SharpLinkVersionDeclineReason.Policy &&
               refusal.Peers.Single() is { ActivationSent: false, DeclineReason: SharpLinkVersionDeclineReason.Policy } peer &&
               peer.Change == declined.Change && NStudyCount(newType, "Created") == 0 &&
               ReferenceEquals(session, NStudyClientSession(client)) &&
               session.CaptureNStudyBinding(a.Contract) == a.BindingId &&
               session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
               session.NStudyBindOperationForDiagnostics == binds,
            "the public server callback can await a real sibling RPC and decline without changing either route");
        trace.Record("PublicServerCallbackRpcThenRejectRetainsOriginalRoutes", a, b);

        var joining = publicClient.JoinVersionChangeAsync(contract, participant, ApproveAfterCheckingSibling, ct).AsTask();
        var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [participant], TimeSpan.FromSeconds(5), Approve, ct).AsTask();
        await Task.WhenAll(joining, replacing).WaitAsync(ct);
        var joined = await joining;
        var replaced = await replacing;
        EnsureReplacementReleased(replaced.Replacement, "a new attempt succeeds after the explicit policy decline");
        var current = session.CaptureNStudyRoute(a.Contract).Acknowledgement;
        Ensure(clientCallbackResult == 102 && joined.Resumed && joined.Change.TransitionId > declined.Change.TransitionId &&
               replaced.Peers.Single() is { ActivationSent: true } activated && activated.Change == joined.Change &&
               current.Target == server.ReadNStudyTarget(newAssembly, a.Contract) &&
               ReferenceEquals(session, NStudyClientSession(client)) &&
               session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
               session.NStudyBindOperationForDiagnostics == binds + 1 &&
               await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4,
            "the public client callback can await a real sibling RPC and resume only the changed route");
        trace.Record("PublicClientCallbackRpcThenApproveResumesChangedRoute", current, b);
    }
}
