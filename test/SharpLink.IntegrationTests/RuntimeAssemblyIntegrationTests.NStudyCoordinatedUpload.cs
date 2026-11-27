using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private const int CoordinatedUploadStreamWindow = 128;
    private const int CoordinatedUploadConnectionWindow = 256;

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCoordinatedReplacementPreservesAdmittedUploadThroughPhysicalExit(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyCoordinatedUploadAsync,
            configureClient: builder => builder.UseRuntime(ConfigureCoordinatedUploadWindows),
            configureServer: builder => builder.UseRuntime(ConfigureCoordinatedUploadWindows));

    private static void ConfigureCoordinatedUploadWindows(SharpLinkRuntimeOptions options)
    {
        options.FlowControl.StreamReceiveWindowBytes = CoordinatedUploadStreamWindow;
        options.FlowControl.ConnectionReceiveWindowBytes = CoordinatedUploadConnectionWindow;
    }

    private static async Task VerifyCoordinatedUploadAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a, b, receiver, sibling) = first;
        ISharpLinkServer publicServer = server;
        ISharpLinkClient publicClient = client;
        var local = publicClient.GetLocalContract(contracts.ContractAssembly, a.Contract);
        var participant = new SharpLinkVersionParticipant(publicServer.GetVersionPeers().Single(), SharpLinkVersionInitiator.Server);
        var connection = NStudyClientConnection(client);
        var module = GetDynamicModule(server, oldAssembly);
        var input = new NStudyInput();
        var flow = ((RpcSessionProtocolState)typeof(RpcSession).GetField("_protocolState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).FlowController!;
        Ensure(session.NegotiatedOptions?.StreamReceiveWindowBytes == CoordinatedUploadStreamWindow &&
               flow.SendConnectionCredit == CoordinatedUploadConnectionWindow,
            "the actual Session starts with the configured bounded upload credit");
        await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && peer.Session.QueuedSendBytes == 0, ct);

        static ValueTask<SharpLinkVersionDecision> Approve(SharpLinkVersionChange _, CancellationToken __)
            => ValueTask.FromResult(SharpLinkVersionDecision.Approve);

        try
        {
            var oldCall = CallNStudyInput(receiver.Service, contracts.ContractType, "client-stream", input, ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 1, ct);
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1 &&
                   peer.NStudyBindings.Snapshot() == (2, 0, 1) &&
                   connection.PendingCalls.NStudyProducerCount == 1 && input.Enumerators == 1 && !oldCall.IsCompleted,
                "the original R1 upload owns both ends before coordinated replacement");
            var joining = publicClient.JoinVersionChangeAsync(local, participant, Approve, ct).AsTask();
            var replacing = publicServer.ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, a.Contract,
                [participant], TimeSpan.FromSeconds(10), Approve, ct).AsTask();
            var resumed = await joining.WaitAsync(ct);
            var r2 = session.CaptureNStudyRoute(a.Contract).Acknowledgement;
            Ensure(resumed.Resumed && r2.Target == server.ReadNStudyTarget(newAssembly, a.Contract) &&
                   r2.Target != a.Target && r2.Authority == a.Authority &&
                   NStudyWire.SlotIndex(r2.BindingId) != NStudyWire.SlotIndex(a.BindingId) &&
                   peer.NStudyBindings.Snapshot() == (2, 1, 1) && !replacing.IsCompleted && !oldCall.IsCompleted,
                "coordinated Resume publishes only R2 for new uploads while the original producer owns R1");
            Ensure((await CallNStudyInput(receiver.Service, contracts.ContractType, "client-stream", Values(11, 13), ct))
                       .SequenceEqual([24]) && NStudyCount(newType, "InputItemsConsumed") == 2 &&
                   NStudyCount(oldType, "InputItemsConsumed") == 1 && await sibling.TouchAsync(1, ct) == 101,
                "the new upload and the unchanged sibling run without moving the original upload");
            trace.Record("FreshR2UploadRunsWhileR1RemainsPinned", a, r2);

            input.ReleaseItems();
            await input.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 2, ct);
            Ensure(!oldCall.IsCompleted && !replacing.IsCompleted && module.RemainingStreams == 1 &&
                   NStudyCount(oldType, "Disposed") == 0 && connection.PendingCalls.NStudyProducerCount == 1,
                "the final producer disposal await still protects the original stream and registration");
            input.ReleaseDisposal();
            Ensure((await oldCall.WaitAsync(ct)).SequenceEqual([10]), "the original R1 handler returns its own upload result");
            var replaced = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(replaced.Replacement, "the old registration drains after its original producer exits");
            await WaitForNStudyAsync(() => CohortPeerDrained(first, connection, 2) &&
                flow.SendConnectionCredit == CoordinatedUploadConnectionWindow && flow.ActiveSendStreamCount == 0, ct);
            Ensure(input.Enumerators == 1 && input.ExitCount == 1 && NStudyCount(oldType, "Disposed") == 1 &&
                   NStudyCount(oldType, "InputStreamExits") == 1 && ReferenceEquals(session, NStudyClientSession(client)) &&
                   session.CaptureNStudyBinding(b.Contract) == b.BindingId && await sibling.TouchAsync(2, ct) == 102,
                "both uploads return their credit and physical owners while the original Session remains Ready");
            trace.Record("R1PhysicalExitReturnsUploadCreditWithoutDisturbingR2", a, r2);
        }
        finally
        {
            input.ReleaseItems();
            input.ReleaseDisposal();
        }
    }
}
