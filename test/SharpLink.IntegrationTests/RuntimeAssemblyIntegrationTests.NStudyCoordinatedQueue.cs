using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCoordinatedCommitDoesNotRetargetAnAlreadyQueuedUnary(string transport)
    {
        var writer = new NStudyOutboundProbe();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, _, ct) => VerifyNStudyCoordinatedQueueAsync(fixture, transport, writer, ct),
            factory => new NStudyFenceTransportFactory(factory, writer));
    }

    private static async Task VerifyNStudyCoordinatedQueueAsync(
        NStudyCoordinationFixture fixture, string transport, NStudyOutboundProbe writer, CancellationToken ct)
    {
        var (server, contracts, r1Assembly, r2Assembly, r1, r2, peer, _, _) = fixture;
        var (client, session, connection, original, sibling, receiver, b) = peer;
        var contract = contracts.ContractType;
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest).Succeeded,
            "shared generated contract metadata for the method observer");
        var unaryId = manifest!.Contracts
            .Single(item => item.ContractId == original.Contract).Methods
            .Single(item => item.Name == "UnaryAsync").MethodId;
        var recorder = new NStudyFactsInterceptor();
        server.ReplaceInterceptors([recorder]);
        server.EnableAdmissionControl(options =>
        {
            options.AddContract(original.Contract, rule => rule.UseConcurrency(1));
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(15);
        });
        var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
        var oldRoute = session.CaptureNStudyRoute(original.Contract);
        var initialBinds = session.NStudyBindOperationForDiagnostics;
        var vote = new NStudyManualDecision();
        var seq = 0;
        ulong queuedId = 0;
        ulong transaction = 0;
        NStudyAck? next = null;
        void Record(string step) => Console.WriteLine("NQUEUE " + JsonSerializer.Serialize(new
        {
            step,
            seq = ++seq,
            transport,
            session = original.Authority.PhysicalSession,
            request = queuedId,
            transaction,
            oldBinding = original.BindingId,
            newBinding = next?.BindingId,
            oldRegistration = original.Target.Registration,
            newRegistration = next?.Target.Registration,
            queued = kernel.QueuedCalls,
            unary = recorder.Visits(unaryId)
        }));

        writer.StartRecording();
        var active = InvokeValueTaskAsync<int>(receiver.Service, contract, "BlockAsync", ct).AsTask();
        Task<int>? queued = null;
        try
        {
            await ReadNStudyTask(r1, "BlockStarted").WaitAsync(ct);
            queued = InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 11, ct).AsTask();
            await WaitForNStudyAsync(() => kernel.QueuedCalls == 1 &&
                writer.Flushed.Count(frame => frame.Binding == original.BindingId) == 2, ct);
            queuedId = writer.Flushed.Last(frame => frame.Binding == original.BindingId).Header.RequestId;
            Ensure(queuedId != 0 && !queued.IsCompleted && !active.IsCompleted && recorder.Visits(unaryId) == 0 &&
                   NStudyCount(r1, "Created") == 1 && NStudyCount(r2, "Created") == 0 &&
                   server.ReadNStudyTarget(r1Assembly, original.Contract) == original.Target,
                "the one normal former R1 Unary has reached the server admission queue behind an actual A service call");
            Ensure(await b.TouchAsync(1, ct) == 101 && session.CaptureNStudyBinding(sibling.Contract) == sibling.BindingId,
                "independent B continues while only A's contract permit is held");
            Record("OnlyOldUnaryParsedAndQueued");

            var joining = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, vote.DecideAsync, ct);
            var replacing = server.StageNStudyReplacementAsync(connection, r1Assembly, r2Assembly, original.Contract,
                NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);
            var change = await vote.Seen.WaitAsync(ct);
            transaction = change.Id;
            Ensure(change.Authority == original.Authority && change.Original == original.Target &&
                   !joining.IsCompleted && !replacing.IsCompleted && !queued.IsCompleted && kernel.QueuedCalls == 1,
                "the same one old parsed waiter is still held when this exact manual change reaches the app");
            Record("ManualDecisionSeesExactPendingOldQueue");
            vote.Approve();

            var activated = await joining.WaitAsync(ct) ?? throw new InvalidOperationException("Expected a current R2 binding.");
            next = activated.Binding;
            await WaitForNStudyAsync(() => writer.Flushed.Any(frame =>
                frame.Control is { Phase: NStudyPhase.Ready } ready && ready.Change == change), ct);
            var observed = writer.Flushed.ToArray();
            var oldIndex = Array.FindIndex(observed, frame => frame.Binding == original.BindingId &&
                frame.Header.RequestId == queuedId);
            var readyIndex = Array.FindIndex(observed, frame =>
                frame.Control is { Phase: NStudyPhase.Ready } ready && ready.Change == change);
            Ensure(oldIndex >= 0 && readyIndex > oldIndex && activated.Change == change &&
                   next.Value.Authority == original.Authority && next.Value.Target == change.Target &&
                   next.Value.Target == server.ReadNStudyTarget(r2Assembly, original.Contract) &&
                   next.Value.Target != original.Target && next.Value.BindingId != original.BindingId &&
                   !oldRoute.IsOpenOn(session) && kernel.QueuedCalls == 1 && !queued.IsCompleted &&
                   !active.IsCompleted && !replacing.IsCompleted &&
                   NStudyCount(r1, "Disposed") == 0 && NStudyCount(r2, "Created") == 0 &&
                   recorder.Visits(unaryId) == 0 && await b.TouchAsync(2, ct) == 102,
                "R2 has actually activated, after the old head and exact Ready, while one old non-invoked server waiter remains");
            Record("ExactReadyAndR2CommitWithOldWaiterStillQueued");

            r1.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await active.WaitAsync(ct) == 42, "previously admitted R1 work finishes normally on its original service");
            await EnsureQueuedSelectionUnavailableAsync(queued);
            var completed = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(completed.Replacement, "original admitted owner has physically exited");
            Ensure(kernel.QueuedCalls == 0 && recorder.Visits(unaryId) == 0 &&
                   NStudyCount(r1, "Disposed") == 1 && NStudyCount(r2, "Created") == 0,
                "released queue can neither execute R1 nor retarget R2 after real commit");
            Record("ReleasedOldWaiterUnavailableWithoutBusiness");

            Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 41, ct) == 42 &&
                   await b.TouchAsync(3, ct) == 103 &&
                   recorder.Visits(unaryId) == 1 && NStudyCount(r2, "Created") == 1 &&
                   ReferenceEquals(NStudyClientSession(client), session) &&
                   session.CaptureNStudyBinding(original.Contract) == next.Value.BindingId &&
                   session.CaptureNStudyBinding(sibling.Contract) == sibling.BindingId &&
                   session.NStudyBindOperationForDiagnostics == initialBinds + 1,
                "one distinct new generated Unary uses only R2 on the original session and B stays unaffected");
            await WaitForNStudyAsync(() => client.PendingCallCount == 0 && connection.ActiveCalls == 0 &&
                kernel.QueuedCalls == 0 && kernel.ActivePermits == 0, ct);
            Record("NewUnaryUsesR2AndQueueAndPermitsReturn");
        }
        finally
        {
            vote.Approve();
            r1.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }
    }
}
