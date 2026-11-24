namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCohortDuplexCancellationDoesNotRetireTheOtherPeer(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            VerifyNStudyCohortDuplexAsync);

    private static async Task VerifyNStudyCohortDuplexAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, lead, followerOrNull, _) = fixture;
        var follower = followerOrNull ?? throw new InvalidOperationException("Expected a second data Session.");
        var contract = contracts.ContractType;
        var leadInput = new NStudyInput();
        var followerInput = new NStudyInput();
        var followerDecision = new NStudyManualDecision();
        using var cancelLead = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leadClient = NStudyClientConnection(lead.Client);
        var followerClient = NStudyClientConnection(follower.Client);
        var original = GetDynamicModule(server, oldAssembly);

        var leadR1 = CallNStudyInput(lead.Receiver.Service, contract, "duplex", leadInput, cancelLead.Token);
        var followerR1 = CallNStudyInput(follower.Receiver.Service, contract, "duplex", followerInput, ct);
        try
        {
            await Task.WhenAll(leadInput.Started, followerInput.Started).WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 2, ct);
            Ensure(lead.A.Authority.PhysicalSession != follower.A.Authority.PhysicalSession &&
                   lead.A.Target == follower.A.Target && lead.A.BindingId == follower.A.BindingId &&
                   original.RemainingStreams == 2 && leadInput.Enumerators == 1 && followerInput.Enumerators == 1 &&
                   lead.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 1) &&
                   follower.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 1),
                "two actual Sessions own independent admitted duplexes even with equal binding numbers");
            trace.Record("EqualBindingsKeepTwoIndependentR1DuplexOwners", lead.A, follower.A);

            const NStudyInitiator initiator = NStudyInitiator.Server;
            var leadBinds = lead.Session.NStudyBindOperationForDiagnostics;
            var followerBinds = follower.Session.NStudyBindOperationForDiagnostics;
            static ValueTask<bool> Approve(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);
            var join = lead.Client.NegotiateNStudyReplacementAsync(initiator, Approve, ct);
            var wait = follower.Client.NegotiateNStudyReplacementAsync(initiator, followerDecision.DecideAsync, ct);
            var replace = server.StageNStudyReplacementOnSessionsAsync(
                [new(lead.ServerConnection, initiator, Approve), new(follower.ServerConnection, initiator, Approve)],
                oldAssembly, newAssembly, lead.A.Contract, TimeSpan.FromSeconds(15), ct);
            var followerTicket = await followerDecision.Seen.WaitAsync(ct);
            var activation = await join.WaitAsync(ct) ?? throw new InvalidOperationException("Lead did not resume.");
            var r2 = activation.Binding;
            await WaitForNStudyAsync(() => follower.Session.NStudyPreparedCount == 1, ct);
            Ensure(r2.Authority == lead.A.Authority && followerTicket.Authority == follower.A.Authority &&
                   r2.Target == followerTicket.Target && !wait.IsCompleted && !replace.IsCompleted &&
                   !follower.ServerConnection.Session.MayBindNStudyTarget(r2.Target) &&
                   lead.Session.NStudyBindOperationForDiagnostics == leadBinds + 1 &&
                   follower.Session.NStudyBindOperationForDiagnostics == followerBinds &&
                   (await CallNStudyInput(lead.Receiver.Service, contract, "duplex", Values(11, 13), ct).WaitAsync(ct))
                       .SequenceEqual([22, 26]) &&
                   NStudyCount(newType, "InputStreamExits") == 1 &&
                   await follower.Sibling.TouchAsync(1, ct) == 101,
                "only lead uses R2; follower still owns its old stream and can call unrelated B");
            trace.Record("LeadUsesR2WhileFollowerWaitsInR1", r2, follower.A);

            followerDecision.Decline();
            Ensure(await wait.WaitAsync(ct) is null && !followerR1.IsCompleted &&
                   !follower.ServerConnection.Session.MayBindNStudyTarget(r2.Target) &&
                   follower.Session.NStudyBindOperationForDiagnostics == followerBinds,
                "ordinary follower decline adds no R2 binding and does not cancel admitted R1");

            var producerToken = leadInput.ProducerToken;
            var handle = producerToken.WaitHandle.SafeWaitHandle;
            cancelLead.Cancel();
            await ExpectNStudyCallerCancellation(leadR1, cancelLead.Token, ct);
            await leadInput.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => lead.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 0) &&
                                           original.RemainingStreams == 1, ct);
            Ensure(producerToken.IsCancellationRequested && !handle.IsClosed && leadInput.ExitCount == 0 &&
                   leadClient.PendingCalls.NStudyProducerCount == 1 && followerClient.PendingCalls.NStudyProducerCount == 1 &&
                   follower.ServerConnection.NStudyBindings.Snapshot() == (1, 1, 1) &&
                   !followerR1.IsCompleted && !replace.IsCompleted && NStudyCount(oldType, "InputStreamExits") == 1 &&
                   await lead.Sibling.TouchAsync(2, ct) == 102 && await follower.Sibling.TouchAsync(2, ct) == 102,
                "lead cancels logically and remotely, but its producer CTS and the independent follower R1 still have owners");
            trace.Record("LeadCancellationKeepsItsProducerAndTheOtherPeerIndependent", r2, follower.A);

            leadInput.ReleaseDisposal();
            await WaitForNStudyAsync(() => leadClient.PendingCalls.NStudyProducerCount == 0 &&
                                           handle.IsClosed && leadInput.ExitCount == 1, ct);
            followerInput.ReleaseItems();
            await followerInput.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 3, ct);
            Ensure(!followerR1.IsCompleted && !replace.IsCompleted && original.RemainingStreams == 1 &&
                   NStudyCount(oldType, "Disposed") == 0 && follower.ServerConnection.NStudyBindings.Snapshot() == (1, 1, 1),
                "the follower alone retains the old registration through its own final producer disposal");
            trace.Record("FollowerStillOwnsR1AfterLeadProducerExits", r2, follower.A);

            followerInput.ReleaseDisposal();
            Ensure((await followerR1.WaitAsync(ct)).SequenceEqual([6, 14]), "follower finishes both original R1 results");
            var result = await replace.WaitAsync(ct);
            EnsureReplacementReleased(result.Replacement, "old cohort group exits physically");
            Ensure(result.Peers.Count == 2 && result.Peers[0].ActivationSent && !result.Peers[1].ActivationSent &&
                   result.Peers[1].DeclineReason == NStudyDeclineReason.Policy && !result.Peers[1].Terminated &&
                   followerInput.ExitCount == 1 && NStudyCount(oldType, "InputStreamExits") == 2 &&
                   NStudyCount(oldType, "Disposed") == 1 &&
                   await InvokeValueTaskAsync<int>(lead.Receiver.Service, contract, "UnaryAsync", 8, ct) == 9 &&
                   await lead.Sibling.TouchAsync(3, ct) == 103 && await follower.Sibling.TouchAsync(3, ct) == 103,
                "old owners exit once; lead R2 and both unrelated routes still work");
            await WaitForNStudyAsync(() => CohortPeerDrained(lead, leadClient, 2) &&
                                           CohortPeerDrained(follower, followerClient, 1), ct);
            trace.Record("BothSessionsDrainWithoutLosingR2OrEitherB", r2, follower.B);
        }
        finally
        {
            followerDecision.Approve();
            leadInput.ReleaseItems();
            leadInput.ReleaseDisposal();
            followerInput.ReleaseItems();
            followerInput.ReleaseDisposal();
        }
    }

    private static bool CohortPeerDrained(NStudyTestPeer peer, ClientConnection client, int open)
        => peer.ServerConnection.NStudyBindings.Snapshot() == (open, 0, 0) &&
           client.PendingCalls.ActiveCount == 0 && client.PendingCalls.NStudyProducerCount == 0 &&
           client.ActiveCallCount == 0 && peer.ServerConnection.ActiveCalls == 0 &&
           peer.Session.StreamManager.ActiveStreamCount == 0 &&
           peer.ServerConnection.Session.StreamManager.ActiveStreamCount == 0;
}
