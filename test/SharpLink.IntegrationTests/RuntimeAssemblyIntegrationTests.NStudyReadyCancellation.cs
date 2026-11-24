namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCallerCanCancelAfterReadyBeforeTheCandidatePublishes(string transport)
    {
        var serverFlush = new NStudyHeldResponseFlush();
        var clientWriter = new NStudyOutboundProbe();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyReadyCancellationAsync(fixture, trace, serverFlush, clientWriter, ct),
            decorateTransport: inner => new NStudyFenceTransportFactory(inner, clientWriter),
            decorateServerTransport: inner => new NStudyHeldResponseListener(inner, serverFlush));
    }

    private static async Task VerifyNStudyReadyCancellationAsync(NStudyCoordinationFixture fixture, NStudyTrace trace,
        NStudyHeldResponseFlush serverFlush, NStudyOutboundProbe clientWriter, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, createClient) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var contract = contracts.ContractType;
        var participant = new SharpLinkVersionParticipant(server.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var local = client.GetLocalContract(contracts.ContractAssembly, a1.Contract);
        var inventory = server.GetLocalVersionInventory();
        var originalRoute = session.CaptureNStudyRoute(a1.Contract);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prepared = new TaskCompletionSource<SharpLinkVersionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vote = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<SharpLinkVersionDecision> ApproveAfterPrepare(SharpLinkVersionChange change, CancellationToken token)
        {
            prepared.TrySetResult(change);
            return await vote.Task.WaitAsync(token);
        }
        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 1, ct) == 2,
            "the original real service is callable before the change");
        var joining = client.JoinVersionChangeAsync(local, participant, ApproveAfterPrepare, caller.Token).AsTask();
        var replacing = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a1.Contract, [participant],
            TimeSpan.FromSeconds(5), Approve, ct).AsTask();
        Task<int>? bResponse = null;
        try
        {
            var proposal = await prepared.Task.WaitAsync(ct);
            await WaitForNStudyAsync(() => peer.Session.QueuedSendBytes == 0, ct);
            serverFlush.Arm();
            bResponse = b.TouchAsync(2, ct).AsTask();
            await serverFlush.Entered.Task.WaitAsync(ct);
            var heldBytes = peer.Session.QueuedSendBytes;
            Ensure(heldBytes > 0 && !bResponse.IsCompleted && originalRoute.IsOpenOn(session),
                "a real server response holds later outgoing bytes before the client approves");
            clientWriter.StartRecording();
            vote.TrySetResult(SharpLinkVersionDecision.Approve);
            await WaitForNStudyAsync(() => clientWriter.Flushed.Any(frame =>
                frame.Control is { Phase: NStudyPhase.Ready } ready && ready.Change.Id == proposal.TransitionId) &&
                peer.Session.QueuedSendBytes > heldBytes, ct);
            Ensure(!joining.IsCompleted && !replacing.IsCompleted && !originalRoute.IsOpenOn(session) &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   NStudyCount(newType, "Created") == 0 && !bResponse.IsCompleted,
                "the actual Ready froze the source, while server confirmation is held before publication");
            trace.Record("ActualReadyLeavesBeforeServerCommitCanProceed", a1, b1);

            caller.Cancel();
            await ExpectNStudyCallerCancellation(joining, caller.Token, ct);
            serverFlush.Release.TrySetResult();
            var aborted = await replacing.WaitAsync(ct);
            var declined = aborted.Peers.Single();
            var after = server.GetLocalVersionInventory();
            Ensure(!aborted.Replacement.Succeeded && declined.Change == proposal && !declined.ActivationSent &&
                   declined.PreparedClientRegistrationId is null && !ct.IsCancellationRequested &&
                   after.PublisherEpoch == inventory.PublisherEpoch && after.Revision == inventory.Revision &&
                   after.Registrations.SequenceEqual(inventory.Registrations) &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   NStudyCount(newType, "Created") == 0 && NStudyCount(oldType, "Disposed") == 0,
                "ordinary caller cancellation after Ready cannot publish or activate the staged generation");
            await peer.SessionTask.WaitAsync(ct);
            trace.Record("CallerCancellationAfterReadyLeavesR1Published", a1, b1);

            await using var fresh = createClient();
            Ensure(fresh.RegisterAssembly(contracts.ContractAssembly).Succeeded, "the fresh peer installs the real contract");
            await fresh.ConnectAsync(ct);
            var freshConnection = NStudyClientConnection(fresh);
            Ensure(!ReferenceEquals(freshConnection.Session, session) &&
                   freshConnection.Session.NStudyPreparedCount == 0 &&
                   freshConnection.Session.NStudyBindOperationForDiagnostics == 0,
                "a fresh physical link inherits no readiness or binding from the aborted Ready");
            var (freshB, b2) = await PrepareNStudySiblingAsync(fresh, b1.Contract, ct);
            var freshA = CreateNStudyExactGenerated(fresh, contract);
            var sameR1 = await freshA.PrepareExact(new(b2.Authority, a1.Contract, a1.Hash, a1.Target),
                TimeSpan.FromSeconds(5), ct);
            Ensure(sameR1.Authority.PhysicalSession != a1.Authority.PhysicalSession &&
                   sameR1.Target == a1.Target && sameR1.Authority == b2.Authority &&
                   freshConnection.Session.NStudyBindOperationForDiagnostics == 2 &&
                   await InvokeValueTaskAsync<int>(freshA.Service, contract, "UnaryAsync", 3, ct) == 4 &&
                   await freshB.TouchAsync(3, ct) == 103 && NStudyCount(newType, "Created") == 0,
                "only explicit fresh prepares restore the unchanged R1 and unrelated B");
            trace.Record("FreshPrepareRestoresR1AndBWithoutInheritingReady", sameR1, b2);
        }
        finally
        {
            vote.TrySetResult(SharpLinkVersionDecision.Reject);
            serverFlush.Release.TrySetResult();
            if (bResponse is not null)
            {
                try { await bResponse.WaitAsync(ct); }
                catch (SharpLinkException) when (!session.IsConnected) { }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !session.IsConnected) { }
            }
        }
    }
}
