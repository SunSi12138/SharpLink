namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCancellationAfterPublicationDoesNotRollBackOrResumeOldLink(string transport)
    {
        var serverWriter = new NStudyOutboundProbe(static frame => frame.Control is { Phase: NStudyPhase.Activated });
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyCancellationAfterPublicationAsync(fixture, trace, serverWriter, ct),
            decorateServerTransport: inner => new NStudyFenceListener(inner, serverWriter));
    }

    private static async Task VerifyNStudyCancellationAfterPublicationAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyOutboundProbe serverWriter, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, createClient) = fixture;
        var (client, session, peer, a1, b1, receiver, _) = first;
        var contract = contracts.ContractType;
        var participant = new SharpLinkVersionParticipant(server.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var local = client.GetLocalContract(contracts.ContractAssembly, a1.Contract);
        var originalRoute = session.CaptureNStudyRoute(a1.Contract);
        var initialBinds = session.NStudyBindOperationForDiagnostics;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prepared = new TaskCompletionSource<SharpLinkVersionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<SharpLinkVersionDecision> ApproveAfterPrepare(SharpLinkVersionChange change, CancellationToken _)
        {
            prepared.TrySetResult(change);
            return ValueTask.FromResult(SharpLinkVersionDecision.Approve);
        }
        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 1, ct) == 2,
            "real R1 serves before the change");
        serverWriter.StartRecording();
        var joining = client.JoinVersionChangeAsync(local, participant, ApproveAfterPrepare, caller.Token).AsTask();
        var replacing = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a1.Contract, [participant],
            TimeSpan.FromSeconds(5), Approve, ct).AsTask();
        try
        {
            var change = await prepared.Task.WaitAsync(ct);
            await serverWriter.FlushEntered.Task.WaitAsync(ct);
            var target = server.ReadNStudyTarget(newAssembly, a1.Contract);
            var atCommit = server.GetLocalVersionInventory();
            Ensure(atCommit.Registrations.Single(registration => registration.ContractId == a1.Contract) == change.Candidate &&
                   target.Registration == change.Candidate.RegistrationId && target != a1.Target &&
                   !joining.IsCompleted && !originalRoute.IsOpenOn(session) &&
                   session.NStudyBindOperationForDiagnostics == initialBinds &&
                   serverWriter.Flushed.All(frame => frame.Control is not { Phase: NStudyPhase.Activated }) &&
                   NStudyCount(newType, "Created") == 0, "R2 is published but the actual activation has not flushed");
            trace.Record("CandidateCommittedWhileActivateIsStillHeld", a1, b1);

            caller.Cancel();
            await ExpectNStudyCallerCancellation(joining, caller.Token, ct);
            serverWriter.Release();
            var settled = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(settled.Replacement, "the committed server outcome survives the caller's cancellation");
            await peer.SessionTask.WaitAsync(ct);
            var after = server.GetLocalVersionInventory();
            Ensure(!ct.IsCancellationRequested && !session.IsConnected &&
                   session.NStudyBindOperationForDiagnostics == initialBinds &&
                   settled.Peers.Single().Change == change &&
                   after.PublisherEpoch == atCommit.PublisherEpoch && after.Revision == atCommit.Revision &&
                   after.Registrations.SequenceEqual(atCommit.Registrations) &&
                   server.ReadNStudyTarget(newAssembly, a1.Contract) == target &&
                   NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 0,
                "the canceled client did not resume, and the published registry did not roll back");
            trace.Record("CallerCancellationLeavesR2PublishedButOldLinkUnresumed", a1, b1);

            await using var fresh = createClient();
            Ensure(fresh.RegisterAssembly(contracts.ContractAssembly).Succeeded, "fresh endpoint owns the original compatible contract");
            await fresh.ConnectAsync(ct);
            var freshSession = NStudyClientSession(fresh);
            var observed = await fresh.QueryPeerVersionInventoryAsync(ct);
            Ensure(observed.Registrations.Contains(change.Candidate) &&
                   observed.Session != participant.Session && freshSession.NStudyPreparedCount == 0 &&
                   freshSession.NStudyBindOperationForDiagnostics == 0, "inventory observes R2 without importing an old grant");
            var (freshB, b2) = await PrepareNStudySiblingAsync(fresh, b1.Contract, ct);
            var freshA = CreateNStudyExactGenerated(fresh, contract);
            var r2 = await freshA.PrepareExact(new(b2.Authority, a1.Contract, a1.Hash, target),
                TimeSpan.FromSeconds(5), ct);
            Ensure(r2.Authority.PhysicalSession != a1.Authority.PhysicalSession && r2.Target == target &&
                   freshSession.NStudyBindOperationForDiagnostics == 2 &&
                   await InvokeValueTaskAsync<int>(freshA.Service, contract, "UnaryAsync", 3, ct) == 4 &&
                   await freshB.TouchAsync(3, ct) == 103 && NStudyCount(newType, "Created") == 1,
                "fresh explicit preparation reaches the committed R2 and independent B");
            trace.Record("FreshPrepareUsesTheCommittedR2OnANewPhysicalLink", r2, b2);
        }
        finally { serverWriter.Release(); }
    }

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
