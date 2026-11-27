namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", true)]
    [Arguments("shared-memory", true)]
    [Arguments("tcp", false)]
    [Arguments("shared-memory", false)]
    public Task NStudyOrdinaryPrepareDoesNotDisconnectAnOverlappingVersionChange(string transport, bool beforeReady)
    {
        var writer = new NStudyOutboundProbe(frame => beforeReady
            ? frame.Header.Type == NStudyWire.BindAck
            : frame.Control is { Phase: NStudyPhase.Activated });
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyBindingControlAsync(fixture, trace, writer, beforeReady, ct),
            decorateServerTransport: listener => new NStudyFenceListener(listener, writer));
    }

    private static async Task VerifyBindingControlAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyOutboundProbe writer, bool beforeReady, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, _, peer, _, _) = fixture;
        ISharpLinkClient client = peer.Client;
        ISharpLinkServer publicServer = server;
        var participant = new SharpLinkVersionParticipant(publicServer.GetVersionPeers().Single(),
            SharpLinkVersionInitiator.Server);
        var local = client.GetLocalContract(contracts.ContractAssembly, peer.A.Contract);
        var extra = client.CreateProxy<IConnectionBehaviorService>();
        var approves = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vote = new TaskCompletionSource<SharpLinkVersionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);
        async ValueTask<SharpLinkVersionDecision> HoldApproval(SharpLinkVersionChange _, CancellationToken token)
        {
            approves.TrySetResult();
            return await vote.Task.WaitAsync(token);
        }

        var initialBinds = peer.Session.NStudyBindOperationForDiagnostics;
        try
        {
            writer.StartRecording();
            var joining = client.JoinVersionChangeAsync(local, participant, beforeReady ? HoldApproval : Approve, ct).AsTask();
            var replacing = StartReplacement();
            if (beforeReady)
            {
                await approves.Task.WaitAsync(ct);
                var preparing = extra.PrepareAsync(TimeSpan.FromSeconds(5), ct).AsTask();
                await writer.FlushEntered.Task.WaitAsync(ct);
                Ensure(!preparing.IsCompleted && peer.Session.NStudyBindOperationForDiagnostics == initialBinds + 1,
                    "ordinary Prepare owns the in-flight Bind before approval");
                vote.TrySetResult(SharpLinkVersionDecision.Approve);
                var refusal = await joining.WaitAsync(ct);
                Ensure(!refusal.Resumed && refusal.DeclineReason == SharpLinkVersionDeclineReason.SourceUnavailable &&
                       peer.Session.IsConnected && peer.Session.CaptureNStudyBinding(peer.A.Contract) == peer.A.BindingId,
                    "local binding occupancy settles this attempt before removing the original grant");
                writer.Release();
                await preparing.WaitAsync(ct);
                var unchanged = await replacing.WaitAsync(ct);
                Ensure(!unchanged.Replacement.Succeeded && unchanged.Peers.Single().DeclineReason == refusal.DeclineReason &&
                       await InvokeValueTaskAsync<int>(peer.Receiver.Service, contracts.ContractType, "UnaryAsync", 1, ct) == 2 &&
                       await peer.Sibling.TouchAsync(1, ct) == 101,
                    "the peer receives the decline and the original Session still serves both prepared contracts");
                trace.Record("PendingPrepareDeclinesBeforeFreezingOriginalRoute", peer.A, peer.B);
                joining = client.JoinVersionChangeAsync(local, participant, Approve, ct).AsTask();
                replacing = StartReplacement();
            }
            else
            {
                await writer.FlushEntered.Task.WaitAsync(ct);
                try
                {
                    await extra.PrepareAsync(TimeSpan.FromSeconds(5), ct);
                    throw new InvalidOperationException("An ordinary Bind must not take activation's binding control.");
                }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted) { }
                Ensure(peer.Session.IsConnected && !joining.IsCompleted &&
                       peer.Session.NStudyBindOperationForDiagnostics == initialBinds,
                    "busy ordinary preparation sends no Bind while the exact activation is outstanding");
                writer.Release();
            }
            var resumed = await joining.WaitAsync(ct);
            var replacement = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(replacement.Replacement, "the accepted attempt releases the original group");
            await extra.PrepareAsync(TimeSpan.FromSeconds(5), ct);
            var r2 = peer.Session.CaptureNStudyRoute(peer.A.Contract).Acknowledgement;
            Ensure(resumed.Resumed && r2.Authority == peer.A.Authority && r2.Target != peer.A.Target &&
                   ReferenceEquals(peer.Session, NStudyClientSession(peer.Client)) &&
                   peer.Session.NStudyBindOperationForDiagnostics == initialBinds + 2 &&
                   await InvokeValueTaskAsync<int>(peer.Receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                   await peer.Sibling.TouchAsync(2, ct) == 102 && await extra.Service.PingAsync(3) == 4,
                "exact R2 and ordinary preparation both complete on the original Session; prepared traffic stays usable");
            trace.Record("VersionAndOrdinaryPrepareFinishOnTheSameSession", r2, peer.B);
        }
        finally
        {
            vote.TrySetResult(SharpLinkVersionDecision.Reject);
            writer.Release();
        }

        Task<SharpLinkServerVersionChangeResult> StartReplacement() =>
            publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, peer.A.Contract,
                [participant], TimeSpan.FromSeconds(10), Approve, ct).AsTask();
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyTimedOutPrepareReleasesItsExactGrantAfterActivation(string transport)
    {
        var clock = new SharpLink.UnitTests.Runtime.ManualTimeProvider();
        var writer = new NStudyOutboundProbe(frame => frame.Control is { Phase: NStudyPhase.Activated });
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, _, _, peer, _, _) = fixture;
                ISharpLinkClient client = peer.Client;
                ISharpLinkServer publicServer = server;
                var participant = new SharpLinkVersionParticipant(publicServer.GetVersionPeers().Single(),
                    SharpLinkVersionInitiator.Server);
                var local = client.GetLocalContract(contracts.ContractAssembly, peer.A.Contract);
                var site = peer.Client.CreateNStudyContractSite<IConnectionBehaviorService>();
                var bound = new TaskCompletionSource<NStudyAck>(TaskCreationOptions.RunContinuationsAsynchronously);
                site.BeforePublishingPreparationForTests = (ack, token) =>
                {
                    bound.TrySetResult(ack);
                    return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token));
                };
                static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
                    => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

                try
                {
                    var preparing = site.PrepareAsync(TimeSpan.FromSeconds(2), ct).AsTask();
                    var ack = await bound.Task.WaitAsync(ct);
                    writer.StartRecording();
                    var joining = client.JoinVersionChangeAsync(local, participant, Approve, ct).AsTask();
                    var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly,
                        peer.A.Contract, [participant], TimeSpan.FromSeconds(10), Approve, ct).AsTask();
                    await writer.FlushEntered.Task.WaitAsync(ct);
                    // Expire the shared Prepare as well as its shorter caller
                    // wait; the version control has a separate longer budget.
                    clock.Advance(RpcSession.NStudyTimeout);
                    try
                    {
                        await preparing.WaitAsync(ct);
                        throw new InvalidOperationException("The ordinary Prepare must honor its deadline.");
                    }
                    catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
                    Ensure(peer.Session.IsConnected && !joining.IsCompleted &&
                           peer.Session.HasNStudyBinding(ack.Contract, ack.BindingId),
                        "a local deadline leaves the installed grant for exact cleanup without interrupting activation");
                    trace.Record("OrdinaryPrepareDeadlineDoesNotCloseTheActivatingSession", peer.A, ack);

                    writer.Release();
                    Ensure((await joining.WaitAsync(ct)).Resumed, "the same physical session resumes after the deadline");
                    EnsureReplacementReleased((await replacing.WaitAsync(ct)).Replacement, "normal activation retires R1");
                    await WaitForNStudyAsync(() => !peer.Session.HasNStudyBinding(ack.Contract, ack.BindingId) &&
                        peer.ServerConnection.NStudyBindings.Snapshot() == (2, 0, 0), ct);
                    var r2 = peer.Session.CaptureNStudyRoute(peer.A.Contract).Acknowledgement;
                    Ensure(r2.Authority == ack.Authority && r2.BindingId != ack.BindingId &&
                           ReferenceEquals(peer.Session, NStudyClientSession(peer.Client)) &&
                           await InvokeValueTaskAsync<int>(peer.Receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                           await peer.Sibling.TouchAsync(2, ct) == 102,
                        "only the timed-out grant is reclaimed; R2 and the prepared sibling keep serving on the same Session");
                    trace.Record("ExpiredGrantIsReleasedWhileTheSameSessionServesR2", r2, peer.B);
                }
                finally
                {
                    site.BeforePublishingPreparationForTests = null;
                    writer.Release();
                }
            },
            configureClient: builder => builder.UseTimeProvider(clock),
            decorateServerTransport: listener => new NStudyFenceListener(listener, writer));
    }
}
