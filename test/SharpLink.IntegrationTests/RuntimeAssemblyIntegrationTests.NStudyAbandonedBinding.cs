using SharpLink.CodecPublicationProbe;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyDeclineAfterClientReadyReturnsTheAbandonedBinding(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyAbandonedBindingAsync);

    private static async Task VerifyAbandonedBindingAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, newType, first, _, _) = fixture;
        var (client, session, serverPeer, a, b, receiver, sibling) = first;
        ISharpLinkServer publicServer = server;
        ISharpLinkClient publicClient = client;
        var contract = publicClient.GetLocalContract(contracts.ContractAssembly, a.Contract);
        var participant = new SharpLinkVersionParticipant(
            publicServer.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var proxy = CreatePreparedGeneratedProxy(publicClient, contracts.ContractType);
        var bindings = serverPeer.NStudyBindings;
        Ensure(bindings.Snapshot() == (2, 0, 0), "only the two original prepared bindings exist");

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        ValueTask<SharpLinkVersionDecision> RegisterIndependentAssemblyThenApprove(
            SharpLinkVersionChange _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Ensure(publicServer.RegisterAssembly(typeof(PublicationProbeManifest).Assembly).Succeeded,
                "an unrelated local registration publishes while the client is deciding");
            return ValueTask.FromResult(SharpLinkVersionDecision.Approve);
        }

        var joining = publicClient.JoinVersionChangeAsync(contract, participant, RegisterIndependentAssemblyThenApprove, ct).AsTask();
        var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
            [participant], TimeSpan.FromSeconds(5), Approve, ct).AsTask();
        await Task.WhenAll(joining, replacing).WaitAsync(ct);
        var declined = await joining;
        var rejected = await replacing;
        Ensure(!declined.Resumed && declined.DeclineReason == SharpLinkVersionDeclineReason.CandidateChanged &&
               !rejected.Replacement.Succeeded && rejected.Replacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
               rejected.Peers.Single() is { ActivationSent: false, PreparedClientRegistrationId: not null } peer &&
               peer.Change == declined.Change && server.ReadNStudyTarget(oldAssembly, a.Contract) == a.Target &&
               NStudyCount(newType, "Created") == 0 && ReferenceEquals(session, NStudyClientSession(client)),
            "the verified Ready cannot commit across a registry change, and the original publication survives");
        Ensure(await sibling.TouchAsync(1, ct) == 101 && session.CaptureNStudyBinding(b.Contract) == b.BindingId,
            "the unaffected contract remains available on the same connection");
        // The sibling response also proves earlier outbound cleanup crossed the ordinary reader.
        var afterDecline = bindings.Snapshot();
        Ensure(afterDecline == (1, 0, 0),
            $"the frozen client no longer owns A, so the server must keep only B, actual={afterDecline}");
        trace.Record("AbortedCommitReturnsTheAbandonedRouteWithoutClosingB", a, b);

        await proxy.PrepareAsync(ct);
        var restored = session.CaptureNStudyRoute(a.Contract).Acknowledgement;
        Ensure(restored.Target == a.Target && restored.BindingId != a.BindingId && bindings.Snapshot() == (2, 0, 0) &&
               await InvokeValueTaskAsync<int>(proxy.Service, contracts.ContractType, "UnaryAsync", 2, ct) == 3 &&
               await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4,
            "explicit Prepare obtains a new grant for the still-current generation with no leftover slot");
        trace.Record("FreshPrepareRestoresTheOriginalPublicationWithoutAccumulation", restored, b);
    }
}
