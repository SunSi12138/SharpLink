namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client", "automatic")]
    [Arguments("tcp", "server", "automatic")]
    [Arguments("tcp", "client", "disabled")]
    [Arguments("tcp", "server", "disabled")]
    [Arguments("shared-memory", "client", "automatic")]
    [Arguments("shared-memory", "server", "automatic")]
    [Arguments("shared-memory", "client", "disabled")]
    [Arguments("shared-memory", "server", "disabled")]
    public Task NStudyLivePolicyAppliesToNextTransactionOnNegotiatedSession(
        string transport, string changedPeer, string firstMode)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyLivePolicyAsync(fixture, trace, changedPeer == "client",
                firstMode == "automatic" ? NStudyCoordinationMode.Automatic : NStudyCoordinationMode.Disabled, ct));

    private static async Task VerifyNStudyLivePolicyAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, bool changeClient,
        NStudyCoordinationMode firstMode, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, serverPeer, a1, bBinding, receiver, b) = first;
        var a = receiver.Service;
        var contract = contracts.ContractType;
        var priorBind = session.NStudyBindOperationForDiagnostics;
        const NStudyInitiator initiator = NStudyInitiator.Client;
        var originalVotes = 0;

        void UpdatePolicy(NStudyCoordinationMode mode)
        {
            if (changeClient)
                client.NStudyVersionCoordinationMode = mode;
            else
                server.NStudyVersionCoordinationMode = mode;
        }

        ValueTask<bool> OriginalManualVote(NStudyChange _, CancellationToken __)
        {
            Interlocked.Increment(ref originalVotes);
            return ValueTask.FromResult(false);
        }

        static ValueTask<bool> Approve(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);

        Task<NStudyClientActivation?> StartClient(Func<NStudyChange, CancellationToken, ValueTask<bool>>? vote)
            => client.NegotiateNStudyReplacementAsync(initiator, vote, ct);

        Task<NStudyServerActivation> StartServer(Func<NStudyChange, CancellationToken, ValueTask<bool>>? vote)
            => server.StageNStudyReplacementAsync(serverPeer, oldAssembly, newAssembly, a1.Contract,
                initiator, vote, TimeSpan.FromSeconds(15), ct);

        // This initiator orders Candidate before Propose. Without the other participant
        // starting, the changed endpoint has admitted the operation but cannot vote yet.
        (Task<NStudyClientActivation?> Client, Task<NStudyServerActivation> Server) AdmitThenUpdate(
            Func<NStudyChange, CancellationToken, ValueTask<bool>>? oldVote, NStudyCoordinationMode update)
        {
            if (changeClient)
            {
                var local = StartClient(oldVote);
                Ensure(!local.IsCompleted, "client holds the old policy before the peer starts staging");
                UpdatePolicy(update);
                return (local, StartServer(Approve));
            }

            var staged = StartServer(oldVote);
            Ensure(!staged.IsCompleted, "server holds the old policy before the client proposes");
            UpdatePolicy(update);
            return (StartClient(Approve), staged);
        }

        async Task<NStudyChange> CheckDeclineAsync(
            (Task<NStudyClientActivation?> Client, Task<NStudyServerActivation> Server) attempt, int value)
        {
            var activation = await attempt.Client.WaitAsync(ct);
            var outcome = await attempt.Server.WaitAsync(ct);
            Ensure(activation is null && !outcome.Replacement.Succeeded &&
                   outcome.DeclineReason == NStudyDeclineReason.Policy &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   NStudyCount(newType, "Created") == 0 &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   !session.LifetimeToken.IsCancellationRequested &&
                   session.CaptureNStudyBinding(a1.Contract) == a1.BindingId &&
                   session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
                   session.NStudyBindOperationForDiagnostics == priorBind &&
                   await InvokeValueTaskAsync<int>(a, contract, "UnaryAsync", value, ct) == value + 1 &&
                   await b.TouchAsync(value, ct) == value + 100,
                "a policy refusal leaves the actual R1 and unrelated B usable on the original Session");
            return outcome.Change ?? throw new InvalidOperationException("Expected the exact staged ticket.");
        }

        var originalCall = InvokeValueTaskAsync<int>(a, contract, "BlockAsync", ct).AsTask();
        try
        {
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
            var oldVote = AdmitThenUpdate(OriginalManualVote, firstMode);
            var refused = await CheckDeclineAsync(oldVote, 1);
            Ensure(originalVotes == 1, "changing mode before a decision does not bypass the admitted manual callback");
            trace.Record("AlreadyAdmittedManualVoteUnaffectedByLocalModeUpdate", a1, bBinding);

            if (firstMode == NStudyCoordinationMode.Disabled)
            {
                var disabled = await CheckDeclineAsync(
                    (StartClient(changeClient ? null : Approve), StartServer(changeClient ? Approve : null)), 2);
                Ensure(disabled.Id > refused.Id && originalVotes == 1, "a newly admitted Disabled policy never approves");
                refused = disabled;
                trace.Record("DisabledPolicyDeclinesNextPairedAttemptWithoutDisconnect", a1, bBinding);
                UpdatePolicy(NStudyCoordinationMode.Automatic);
            }

            var automatic = AdmitThenUpdate(oldVote: null, NStudyCoordinationMode.Manual);
            var activated = await automatic.Client.WaitAsync(ct) ??
                            throw new InvalidOperationException("Expected the admitted Automatic operation to succeed.");
            var a2 = activated.Binding;
            server.NStudyVersionCoordinationMode = client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Disabled;
            Ensure(activated.Change.Id > refused.Id && a2.Authority == a1.Authority &&
                   a2.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) && a2.Target != a1.Target &&
                   !serverPeer.NStudyBindings.HasOpenTarget(a1.Target) && !originalCall.IsCompleted &&
                   !automatic.Server.IsCompleted && session.NStudyBindOperationForDiagnostics == priorBind + 1 &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   await InvokeValueTaskAsync<int>(a, contract, "UnaryAsync", 3, ct) == 4 &&
                   await b.TouchAsync(3, ct) == 103,
                "the captured Automatic scope completes after a switch to Manual; disabling during drain never reopens R1");
            trace.Record("CutBindingAndUnrelatedRoutesSurviveModeChangeDuringDrain", a1, a2);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await originalCall.WaitAsync(ct) == 42, "the admitted R1 call physically completes on its owner");
            var final = await automatic.Server.WaitAsync(ct);
            EnsureReplacementReleased(final.Replacement, "the original registration drains after the mode update");
            Ensure(final.Change == activated.Change && NStudyCount(oldType, "Disposed") == 1 &&
                   NStudyCount(newType, "Created") == 1 && !serverPeer.NStudyBindings.HasOpenTarget(a1.Target) &&
                   session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
                   ReferenceEquals(session, NStudyClientSession(client)) && await b.TouchAsync(4, ct) == 104,
                "switching policy neither reopens a cut generation nor moves B to another Session");
            trace.Record("CutOriginalFinishesOnceAfterPolicyUpdate", a1, a2);
        }
        finally
        {
            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }
    }
}
