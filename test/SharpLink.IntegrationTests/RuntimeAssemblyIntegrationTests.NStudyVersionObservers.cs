using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client")]
    [Arguments("tcp", "server")]
    [Arguments("shared-memory", "client")]
    [Arguments("shared-memory", "server")]
    public Task NStudyObserverBacklogAndFaultCannotUndoARealSwitch(string transport, string slowSide)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyObserversAsync(fixture, trace, slowSide == "client", ct));

    private static async Task VerifyNStudyObserversAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, bool slowClient, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<NStudyVersionNotice>();
        var independent = new ConcurrentQueue<NStudyVersionNotice>();
        var priorBind = session.NStudyBindOperationForDiagnostics;
        async ValueTask SlowObserver(NStudyVersionNotice notice, CancellationToken token)
        {
            delivered.Enqueue(notice);
            if (entered.TrySetResult(token))
            {
                await release.Task;
                throw new InvalidOperationException("The application's notification handler failed.");
            }
            // The actual observer is free to call ordinary RPCs; no registry or wire lane is held.
            Ensure(await b.TouchAsync(10, token) == 110, "an observer callback can await an unrelated real call");
        }

        ValueTask IndependentObserver(NStudyVersionNotice notice, CancellationToken _)
        {
            independent.Enqueue(notice);
            return ValueTask.CompletedTask;
        }

        var slow = slowClient ? client.ConfigureNStudyVersionObserver(SlowObserver) :
            server.ConfigureNStudyVersionObserver(SlowObserver);
        var fast = slowClient ? server.ConfigureNStudyVersionObserver(IndependentObserver) :
            client.ConfigureNStudyVersionObserver(IndependentObserver);

        Task<NStudyClientActivation?> ClientAttempt(bool approve) =>
            client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, (_, _) => ValueTask.FromResult(approve), ct);
        Task<NStudyServerActivation> ServerAttempt() =>
            server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract,
                NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);

        async Task RefuseAsync()
        {
            var clientAttempt = ClientAttempt(approve: false);
            var serverAttempt = ServerAttempt();
            Ensure(await clientAttempt.WaitAsync(ct) is null, "ordinary Manual approval declines");
            var result = await serverAttempt.WaitAsync(ct);
            Ensure(!result.Replacement.Succeeded && result.Change is not null &&
                   result.DeclineReason == NStudyDeclineReason.Policy,
                "declined negotiation remains a structured result");
            await WaitForNStudyAsync(() => fast.Snapshot() is { Active: false, Pending: 0 }, ct);
        }

        try
        {
            await RefuseAsync();
            var callbackToken = await entered.Task.WaitAsync(ct);
            Ensure(delivered.TryPeek(out var firstNotice) && firstNotice.Kind == NStudyVersionNoticeKind.Proposed &&
                   firstNotice.Change.Authority == a1.Authority && !firstNotice.LocalPublicationOccurred,
                "the actual endpoint observes its exact proposal before publication");
            for (var index = 0; index < NStudyVersionObservationQueue.Capacity / 2; index++)
                await RefuseAsync();

            var full = slow.Snapshot();
            Ensure(full.Active && full.Pending == NStudyVersionObservationQueue.Capacity &&
                   full.Dropped == 1 && delivered.Count == 1 && full.Faults == 0 &&
                   await b.TouchAsync(1, ct) == 101,
                "normal refused attempts fill the real queue; extra observation is dropped and business continues");
            trace.Record("OnePhysicallyHeldObserverDoesNotBlockBusinessOrBoundedQueue", a1, b1);

            var advancing = ClientAttempt(approve: true);
            var replacing = ServerAttempt();
            var activation = await advancing.WaitAsync(ct) ?? throw new InvalidOperationException("Expected exact R2.");
            var replaced = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(replaced.Replacement, "actual committed R2 with old-code retirement");
            var committed = slow.Snapshot();
            var skippedOnCommit = slowClient ? 2 : 3;
            Ensure(committed.Active && committed.Pending == NStudyVersionObservationQueue.Capacity &&
                   committed.Dropped == full.Dropped + skippedOnCommit && delivered.Count == 1 &&
                   activation.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1 &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                   await b.TouchAsync(2, ct) == 102,
                "actual commit and prepared RPC do not wait for or rely on an observation slot");
            trace.Record("ActualRegistryPublishesR2AndBWorksWhileObservationQueueIsFull", a1, activation.Binding);

            await WaitForNStudyAsync(() => callbackToken.IsCancellationRequested && slow.Snapshot().TimedOut == 1 &&
                independent.Any(notice => notice.Kind == NStudyVersionNoticeKind.Completed &&
                    notice.Change == activation.Change && notice.LocalPublicationOccurred), ct);
            Ensure(slow.Snapshot().Active && delivered.Count == 1,
                "observer timeout signals cancellation but does not spawn another physical callback");
            release.TrySetResult();
            await WaitForNStudyAsync(() => slow.Snapshot() is { Active: false, Pending: 0, Faults: 1 }, ct);

            var observed = delivered.ToArray();
            var recovered = await client.QueryNStudyRemoteInventoryAsync(ct);
            Ensure(observed.Length == NStudyVersionObservationQueue.Capacity + 1 &&
                   observed.All(notice => notice.Change != activation.Change) &&
                   observed.Select(notice => notice.Change.Id).SequenceEqual(
                       observed.Select(notice => notice.Change.Id).Order()) &&
                   independent.Any(notice => notice.Kind == NStudyVersionNoticeKind.Declined &&
                       notice.DeclineReason == NStudyDeclineReason.Policy && !notice.LocalPublicationOccurred) &&
                   recovered.Select(a1.Contract).Target == activation.Binding.Target &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   await b.TouchAsync(3, ct) == 103 &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
                   NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 1,
                "actual observer exception is isolated; queued notices run in order and inventory repairs missed events without rollback");
            trace.Record("LateObserverFaultLeavesCommittedRegistryAndRoutesUnchanged", a1, activation.Binding);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("client")]
    [Arguments("server")]
    public Task NStudyObserverCanAwaitEndpointStopWithoutSelfJoining(string side)
        => WithNStudyCoordinationPeersAsync("tcp", NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyObserverStopAsync(fixture, trace, side == "client", ct));

    private static async Task VerifyNStudyObserverStopAsync(
        NStudyCoordinationFixture fixture, NStudyTrace trace, bool stopClient, CancellationToken ct)
    {
        var (server, _, oldAssembly, newAssembly, _, _, first, _, _) = fixture;
        var (client, _, peer, a1, b1, _, _) = first;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Observe(NStudyVersionNotice _, CancellationToken token)
        {
            entered.SetResult(token);
            await startStop.Task;
            try
            {
                if (stopClient)
                    await client.StopAsync();
                else
                    await server.StopAsync(TimeSpan.Zero);
                stopReturned.TrySetResult();
            }
            catch (Exception error)
            {
                stopReturned.TrySetException(error);
            }
            await physicalExit.Task;
        }
        var queue = stopClient ? client.ConfigureNStudyVersionObserver(Observe) :
            server.ConfigureNStudyVersionObserver(Observe);

        try
        {
            var rejected = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server,
                static (_, _) => ValueTask.FromResult(false), ct);
            var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract,
                NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);
            Ensure(await rejected.WaitAsync(ct) is null && !(await replacing.WaitAsync(ct)).Replacement.Succeeded,
                "a normal declined transaction is already settled before observer reentry");
            var token = await entered.Task.WaitAsync(ct);
            Ensure(queue.Snapshot() is { Active: true, Pending: 1 }, "one notification still waits");
            startStop.TrySetResult();
            await stopReturned.Task.WaitAsync(ct);
            await WaitForNStudyAsync(() => token.IsCancellationRequested, ct);
            Ensure(queue.Snapshot() is { Sealed: true, Active: true, Pending: 0, Dropped: 1 } &&
                   !queue.Completion.IsCompleted,
                "Stop closes and discards pending work without joining itself or pretending callback physically exited");
            physicalExit.TrySetResult();
            await queue.Completion.WaitAsync(ct);
            Ensure(queue.Snapshot() is { Sealed: true, Active: false, Pending: 0, Faults: 0 },
                "actual callback physical exit completes the sealed observer lifetime");
            trace.Record("ReentrantEndpointStopKeepsOnePhysicalObserverOwnerUntilExit", a1, b1);
        }
        finally
        {
            startStop.TrySetResult();
            physicalExit.TrySetResult();
        }
    }
}
