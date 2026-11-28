using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyStoppingClientJoinsCancelledUploadWithoutAffectingAnotherPeer(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            VerifyNStudyStoppingClientAsync,
            configureClient: builder => builder.UseRuntime(ConfigureCoordinatedUploadWindows),
            configureServer: builder => builder.UseRuntime(ConfigureCoordinatedUploadWindows));

    private static async Task VerifyNStudyStoppingClientAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, originalAssembly, _, originalType, _, first, other, _) = fixture;
        var staying = other ?? throw new InvalidOperationException("Expected an independent second client.");
        var (client, session, peer, a, b, receiver, sibling) = first;
        var aModule = GetDynamicModule(client, contracts.ContractAssembly);
        var serverModule = GetDynamicModule(server, originalAssembly);
        var connection = NStudyClientConnection(client);
        var state = (RpcSessionProtocolState)typeof(RpcSession)
            .GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var flow = state.FlowController!;
        var firstIdentity = server.GetVersionPeers().Single(x => x.PhysicalSessionId == a.Authority.PhysicalSession);
        var otherIdentity = server.GetVersionPeers().Single(x => x != firstIdentity);
        var input = new NStudyInput();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var upload = CallNStudyInput(receiver.Service, contracts.ContractType, "client-stream", input, cancel.Token);
            await input.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(originalType, "InputItemsConsumed") == 1, ct);
            var producerToken = input.ProducerToken;
            var producerHandle = producerToken.WaitHandle.SafeWaitHandle;
            Ensure(a.Authority != staying.A.Authority && connection.PendingCalls.NStudyProducerCount == 1 &&
                   aModule.RemainingStreams > 0 && serverModule.RemainingStreams == 1 &&
                   await sibling.TouchAsync(1, ct) == 101 && await staying.Sibling.TouchAsync(2, ct) == 102,
                "the original generated upload has separate real client/server owners and both peers can call B");
            trace.Record("RealUploadBeginsBeforeClientShutdown", a, staying.A);

            await cancel.CancelAsync();
            await ExpectNStudyCallerCancellation(upload, cancel.Token, ct);
            await input.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => serverModule.RemainingStreams == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            Ensure(connection.PendingCalls.ActiveCount == 0 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   producerToken.IsCancellationRequested && !producerHandle.IsClosed && aModule.RemainingStreams > 0 &&
                   NStudyCount(originalType, "InputStreamExits") == 1 && await sibling.TouchAsync(3, ct) == 103,
                "ordinary cancellation clears the remote stream without discarding the local physical producer");

            ISharpLinkClient endpoint = client;
            var stopping = endpoint.StopAsync(ct).AsTask();
            // Wait on actual server transport closure, not the internal counters under test.
            await peer.SessionTask.WaitAsync(ct);
            var closedBindings = peer.NStudyBindings.Snapshot();
            Ensure(server.GetVersionPeers().SequenceEqual([otherIdentity]) && closedBindings == (0, 0, 0) &&
                   flow.ActiveSendStreamCount == 0 && flow.RetainedSendStreamCount == 0,
                $"closed peer must return route and credit state: bindings={closedBindings}, " +
                $"flow={flow.ActiveSendStreamCount}/{flow.RetainedSendStreamCount}, stop-completed={stopping.IsCompleted}");
            Ensure(!stopping.IsCompleted && client.State == SharpLinkConnectionState.Draining &&
                   connection.PendingCalls.NStudyProducerCount == 1 && aModule.RemainingStreams > 0 &&
                   !producerHandle.IsClosed && input.ExitCount == 0 &&
                   staying.Session.CaptureNStudyBinding(staying.B.Contract) == staying.B.BindingId &&
                   await staying.Sibling.TouchAsync(4, ct) == 104,
                "stopping closes only its own session and credit tables but retains the real producer, module and CTS until exit");
            trace.Record("ClientStopClosesRouteButJoinsPhysicalUploadOwner", a, staying.B);

            input.ReleaseDisposal();
            await stopping.WaitAsync(ct);
            Ensure(client.State == SharpLinkConnectionState.Stopped && input.Enumerators == 1 && input.ExitCount == 1 &&
                   producerHandle.IsClosed && aModule.State == SharpLinkDynamicModuleState.Released &&
                   aModule.RemainingStreams == 0 && connection.ActiveCallCount == 0 &&
                   session.StreamManager.ActiveStreamCount == 0 && peer.NStudyBindings.Snapshot() == (0, 0, 0) &&
                   server.GetVersionPeers().SequenceEqual([otherIdentity]) &&
                   await InvokeValueTaskAsync<int>(staying.Receiver.Service, contracts.ContractType, "UnaryAsync", 5, ct) == 6,
                "physical exit releases the stopped client exactly once; the other client stays on its original prepared route");
            trace.Record("StoppedClientRetiresProducerOnlyAfterPhysicalCleanup", a, staying.A);
        }
        finally
        {
            input.ReleaseItems();
            input.ReleaseDisposal();
        }
    }
}
