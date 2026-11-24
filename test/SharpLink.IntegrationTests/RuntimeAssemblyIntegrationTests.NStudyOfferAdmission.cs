namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyUnpairedServerOfferDeclinesBeforeCandidateAndKeepsOldRoutes(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyNStudyServerOfferAsync);

    private static async Task VerifyNStudyServerOfferAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, peer, _, _) = fixture;
        var (client, session, connection, a1, b1, receiver, b) = peer;
        var a = receiver.Service;
        var priorBind = session.NStudyBindOperationForDiagnostics;
        var decisions = 0;
        Ensure(session.SupportsNStudyOfferAdmission && connection.Session.SupportsNStudyOfferAdmission,
            "actual peers negotiate the optional offer entry");
        ValueTask<bool> Approve(NStudyChange _, CancellationToken __)
        {
            Interlocked.Increment(ref decisions);
            return ValueTask.FromResult(true);
        }
        Task<NStudyServerActivation> Offer() => server.StageNStudyReplacementAsync(connection, oldAssembly,
            newAssembly, a1.Contract, NStudyInitiator.Server, Approve, TimeSpan.FromSeconds(15), ct);

        using (var leaveBeforeOffer = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var enrollment = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, Approve, leaveBeforeOffer.Token);
            Ensure(!enrollment.IsCompleted, "ordinary client enrollment is waiting for the first offer");
            leaveBeforeOffer.Cancel();
            try
            {
                _ = await enrollment.WaitAsync(ct);
                throw new InvalidOperationException("Expected caller cancellation of the unclaimed enrollment.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            Ensure(ReferenceEquals(session, NStudyClientSession(client)) && await b.TouchAsync(1, ct) == 101,
                "a canceled unclaimed enrollment does not create an ambiguous physical transaction");
        }

        var expected = new[]
        {
            (NStudyCoordinationMode.Disabled, NStudyDeclineReason.PeerDisabled),
            (NStudyCoordinationMode.Manual, NStudyDeclineReason.ApprovalRequired),
            (NStudyCoordinationMode.Automatic, NStudyDeclineReason.PeerNotEnrolled)
        };
        foreach (var (mode, reason) in expected)
        {
            client.NStudyVersionCoordinationMode = mode;
            var pending = Offer();
            if (mode != NStudyCoordinationMode.Disabled)
            {
                await WaitForNStudyAsync(() => session.NStudyOfferPendingForDiagnostics, ct);
                Ensure(!pending.IsCompleted && await b.TouchAsync(2, ct) == 102,
                    "the actual receive loop can dispatch B during a bounded offer rendezvous");
            }
            var denied = await pending.WaitAsync(TimeSpan.FromSeconds(3), ct);
            Ensure(!denied.Replacement.Succeeded && denied.DeclineReason == reason && denied.Change is not null &&
                   decisions == 0 && NStudyCount(newType, "Created") == 0 &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   session.CaptureNStudyBinding(a1.Contract) == a1.BindingId &&
                   session.CaptureNStudyBinding(b1.Contract) == b1.BindingId &&
                   session.NStudyBindOperationForDiagnostics == priorBind &&
                   !session.NStudyOfferPendingForDiagnostics &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                   await b.TouchAsync(3, ct) == 103,
                "no application participation gives a scoped refusal without running either policy or disconnecting");
            trace.Record($"Unpaired{mode}OfferRefusedBeforeCandidate", a1, b1);
        }

        client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
        var stagedFirst = Offer();
        await WaitForNStudyAsync(() => session.NStudyOfferPendingForDiagnostics, ct);
        var joined = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, Approve, ct);
        var activated = await joined.WaitAsync(ct) ?? throw new InvalidOperationException("Expected actual client enrollment.");
        var installed = await stagedFirst.WaitAsync(ct);
        EnsureReplacementReleased(installed.Replacement, "a server-first offer enrolls a participating client and replaces once");
        Ensure(decisions == 2 && activated.Binding.Authority == a1.Authority &&
               activated.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
               session.NStudyBindOperationForDiagnostics == priorBind + 1 &&
               !connection.NStudyBindings.HasOpenTarget(a1.Target) &&
               ReferenceEquals(session, NStudyClientSession(client)) &&
               await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
               await b.TouchAsync(4, ct) == 104 && NStudyCount(oldType, "Disposed") == 1,
            "a later explicit participant completes the ordinary exact commit on its original Session");
        trace.Record("ExplicitClientEnrollmentCompletesAfterEarlierScopedRefusals", a1, activated.Binding);
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyPrimaryOfferRefusalDoesNotAbandonEnrolledFollower(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            VerifyNStudyOfferCohortAsync);

    private static async Task VerifyNStudyOfferCohortAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, _, oldAssembly, newAssembly, _, _, lead, followerOrNull, _) = fixture;
        var follower = followerOrNull ?? throw new InvalidOperationException("Expected a second real peer.");
        static ValueTask<bool> Approve(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);
        var followers = follower.Client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, Approve, ct);
        var replaced = await server.StageNStudyReplacementOnSessionsAsync([
            new(lead.ServerConnection, NStudyInitiator.Server, Approve),
            new(follower.ServerConnection, NStudyInitiator.Server, Approve)
        ], oldAssembly, newAssembly, lead.A.Contract, TimeSpan.FromSeconds(15), ct);
        Ensure(await followers.WaitAsync(ct) is null && !replaced.Replacement.Succeeded &&
               replaced.Peers.Count == 2 && replaced.Peers[0].DeclineReason == NStudyDeclineReason.ApprovalRequired &&
               replaced.Peers[1].DeclineReason == NStudyDeclineReason.CandidateChanged &&
               replaced.Peers.All(outcome => !outcome.ActivationSent && !outcome.Terminated) &&
               server.ReadNStudyTarget(oldAssembly, lead.A.Contract) == lead.A.Target &&
               ReferenceEquals(lead.Session, NStudyClientSession(lead.Client)) &&
               ReferenceEquals(follower.Session, NStudyClientSession(follower.Client)) &&
               lead.Session.CaptureNStudyBinding(lead.A.Contract) == lead.A.BindingId &&
               follower.Session.CaptureNStudyBinding(follower.A.Contract) == follower.A.BindingId &&
               await lead.Sibling.TouchAsync(5, ct) == 105 && await follower.Sibling.TouchAsync(6, ct) == 106,
            "primary nonparticipation settles an already-enrolled follower before any abandoned Candidate");
        trace.Record("PrimaryRefusalSettlesEnrolledFollowerAcrossRealSessions", lead.A, follower.A);
    }
}
