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
