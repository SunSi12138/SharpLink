using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task RunPublicationControlsAsync(string transport, CancellationToken token)
    {
        await EarlyAckAndOrderAsync(transport, token);
        await BackpressureAndWaiterCancelAsync(transport, token);
        await OriginalBudgetAsync(transport, token);
        await DrainOwnershipAsync(transport, token);
    }

    private static async Task EarlyAckAndOrderAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        await f.ConnectAsync(token);
        var state = f.Connections.Single().StaticBindings;
        var before = Signal(); var allowSend = Signal(); var enqueued = Signal(); var allowCommit = Signal();
        var ackSeen = Signal(); var secondBefore = Signal();
        var sends = new List<ulong>();
        state.PublicationTestHook = async (id, phase, ct) =>
        {
            if (phase == StaticBindingPublicationPhase.BeforeSend)
            {
                lock (sends) sends.Add(id);
                if (id == 1) { before.TrySetResult(); await allowSend.Task.WaitAsync(ct); }
                else secondBefore.TrySetResult();
            }
            if (phase == StaticBindingPublicationPhase.Enqueued && id == 1)
            { enqueued.TrySetResult(); await allowCommit.Task.WaitAsync(ct); }
        };
        state.AckObservedTestHook = id => { if (id == 1) ackSeen.TrySetResult(); };
        try
        {
            var first = state.BindAsync(f.Contract.ContractId, f.Hash);
            await before.Task.WaitAsync(token);
            var same = state.BindAsync(f.Contract.ContractId, f.Hash);
            var second = state.BindAsync(long.MaxValue, f.Hash);
            Check(ReferenceEquals(first, same) && state.PendingCount == 2, "control duplicate demand shares one operation");
            Check(state.Sent == 0 && !secondBefore.Task.IsCompleted, "control later Bind cannot overtake predecessor");
            allowSend.TrySetResult();
            await enqueued.Task.WaitAsync(token);
            await ackSeen.Task.WaitAsync(token); // Ack traversed the real server and parser.
            Check(!first.IsCompleted && state.CompletedCount == 0, "control real early Ack cannot commit before publication");
            Check(!secondBefore.Task.IsCompleted && state.Sent == 1, "control publication order remains closed after early Ack");
            allowCommit.TrySetResult();
            var ack = await first.WaitAsync(token);
            await ExpectCode(() => second, SharpLinkErrorCode.FailedPrecondition, "control ordered second Bind gets its own refusal");
            Check(sends.SequenceEqual(new ulong[] { 1, 2 }) && state.PendingCount == 0,
                "control increasing wire operations retire their correlation");
            var rpc = f.Client.CreateProxy<ISliceRpc>();
            await rpc.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Check(await rpc.Service.PingAsync() == 42 && state.Sent == 2, "control committed early Ack enables exact generated route");
            var duplicate = Signal();
            state.AckObservedTestHook = id => { if (id == 1) duplicate.TrySetResult(); };
            await StaticBindingWire.SendAckAsync(f.ServerConnections.Single().Session, 1, ack, token);
            await duplicate.Task.WaitAsync(token);
            Check(state.CompletedCount == 1 && state.PendingCount == 0 && await rpc.Service.PingAsync() == 42,
                "control duplicate real Ack cannot create another binding");
        }
        finally { allowSend.TrySetResult(); allowCommit.TrySetResult(); }
    }

    private static async Task BackpressureAndWaiterCancelAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        await f.ConnectAsync(token);
        await f.FillQueueAsync(token);
        var connection = f.Connections.Single();
        var state = connection.StaticBindings;
        var binding = state.BindAsync(f.Contract.ContractId, f.Hash);
        var drain = connection.Session.CaptureBackpressuredSendDrain();
        Check(!binding.IsCompleted && !drain.IsCompleted && state.Sent == 0 && connection.Session.QueuedSendBytes == 1020,
            "control Bind genuinely waits for send-pump capacity");
        using var canceled = new CancellationTokenSource();
        var rpc = f.Client.CreateProxy<ISliceRpc>();
        var one = rpc.PrepareAsync(TimeSpan.FromSeconds(5), canceled.Token).AsTask();
        var other = rpc.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask();
        canceled.Cancel();
        await Expect<OperationCanceledException>(() => one, "control backpressured waiter cancellation is isolated");
        Check(!other.IsCompleted && !binding.IsCompleted && f.Client.BindingBudget.Operations == 1,
            "control waiter cancellation retains shared Bind owner");
        f.Transport.Outputs.Single().Release();
        await binding.WaitAsync(token);
        await other.WaitAsync(token);
        await drain.WaitAsync(token);
        await connection.Session.FlushSendQueueAsync(token);
        Check(state.Sent == 1 && state.PendingCount == 0 && connection.Session.QueuedSendBytes == 0 &&
            f.Client.BindingBudget.Operations == 0 && await rpc.Service.PingAsync() == 42,
            "control capacity release completes original Bind and returns bytes");
    }

    private static async Task OriginalBudgetAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        await f.ConnectAsync(token);
        await f.FillQueueAsync(token);
        var state = f.Connections.Single().StaticBindings;
        var publication = Signal(); var holdCommit = Signal(); var ackSeen = Signal();
        state.PublicationTestHook = async (_, phase, ct) =>
        {
            if (phase == StaticBindingPublicationPhase.Enqueued)
            { publication.TrySetResult(); await holdCommit.Task.WaitAsync(ct); }
        };
        state.AckObservedTestHook = _ => ackSeen.TrySetResult();
        try
        {
            var first = state.BindAsync(f.Contract.ContractId, f.Hash);
            f.Clock.Advance(TimeSpan.FromSeconds(6));
            Check(!first.IsCompleted && state.Sent == 0, "control six seconds of pressure consume original Bind budget");
            f.Transport.Outputs.Single().Release();
            await publication.Task.WaitAsync(token);
            await ackSeen.Task.WaitAsync(token);
            Check(!first.IsCompleted && state.CompletedCount == 0, "control Ack during held commit remains unready");
            f.Clock.Advance(TimeSpan.FromSeconds(4));
            await ExpectCode(() => first, SharpLinkErrorCode.DeadlineExceeded,
                "control backpressure and Ack share original ten-second deadline");
            Check(state.PendingCount == 0 && state.CompletedCount == 0 && f.Client.BindingBudget.Bindings == 0 &&
                f.Client.BindingBudget.Operations == 0, "control timed-out early Ack releases client reservations");
            var late = Signal();
            state.AckObservedTestHook = _ => late.TrySetResult();
            // The server entry exists, but a timed-out client operation must never revive.
            var serverState = f.ServerConnections.Single();
            var registry = Field<SharpLink.Server.ServerServiceModuleRegistry>(f.Server, "_serviceModuleRegistry");
            var table = serverState.StaticBindings;
            var method = f.Contract.Methods.Single(m => m.Name == nameof(ISliceRpc.PingAsync));
            var status = table.Resolve((1UL << 32) | 1, method.MethodId, ProtocolV2FrameFlags.HasReturn, out var registration, out _);
            if (status != StaticBindingFailure.None) throw new InvalidOperationException("Expected installed server binding.");
            var ack = new StaticBindingAck(StaticBindingFailure.None, f.Contract.ContractId, (1UL << 32) | 1,
                registration.StaticBindingRegistrationId, registry.GenerationStorage);
            await StaticBindingWire.SendAckAsync(serverState.Session, 1, ack, token);
            await late.Task.WaitAsync(token);
            Check(state.CompletedCount == 0 && state.PendingCount == 0, "control late real Ack cannot revive timed-out operation");
            state.PublicationTestHook = null;
            var replacement = await state.BindAsync(f.Contract.ContractId, f.Hash).WaitAsync(token);
            Check(replacement.Token != ack.Token && state.Sent == 2, "control retry demand gets distinct nonrecycled server token");
        }
        finally { holdCommit.TrySetResult(); }

        // A second subject on a fresh fixture tests ordered waiting, not an Ack-wait reset.
        await using var ordered = new ControlFixture(transport);
        await ordered.ConnectAsync(token);
        await ordered.FillQueueAsync(token);
        var orderedState = ordered.Connections.Single().StaticBindings;
        var a = orderedState.BindAsync(ordered.Contract.ContractId, ordered.Hash);
        ordered.Clock.Advance(TimeSpan.FromSeconds(6));
        var b = orderedState.BindAsync(long.MaxValue, ordered.Hash);
        ordered.Clock.Advance(TimeSpan.FromSeconds(4));
        await ExpectCode(() => a, SharpLinkErrorCode.DeadlineExceeded, "control blocked predecessor expires at its own deadline");
        Check(!b.IsCompleted, "control successor retains only its independently remaining budget");
        ordered.Clock.Advance(TimeSpan.FromSeconds(6));
        await ExpectCode(() => b, SharpLinkErrorCode.DeadlineExceeded, "control ordered wait does not restart successor deadline");
        Check(orderedState.Sent == 0 && orderedState.PendingCount == 0 && ordered.Client.BindingBudget.Operations == 0,
            "control canceled capacity sends never emit Bind or leak operation quota");
    }

    private static async Task DrainOwnershipAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        await f.ConnectAsync(token);
        var connection = f.Connections.Single();
        var state = connection.StaticBindings;
        var entered = Signal(); var cleanup = Signal(); var release = Signal();
        state.PublicationTestHook = async (_, phase, ct) =>
        {
            if (phase != StaticBindingPublicationPhase.BeforeSend) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cleanup.TrySetResult(); await release.Task; } // Physical cleanup deliberately remains owned.
        };
        try
        {
            var binding = state.BindAsync(f.Contract.ContractId, f.Hash);
            await entered.Task.WaitAsync(token);
            var rpc = f.Client.CreateProxy<ISliceRpc>();
            var prepare = rpc.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask();
            var stop = f.Client.StopAsync().AsTask();
            var disposal = connection.DisposeAsync().AsTask();
            await cleanup.Task.WaitAsync(token);
            Check(!stop.IsCompleted && !disposal.IsCompleted && !binding.IsCompleted && f.Client.BindingBudget.Operations == 1 &&
                f.Client.BindingBudget.Bindings == 1, "control Stop retains publication cleanup and quotas until physical exit");
            await ExpectCode(() => prepare, SharpLinkErrorCode.ConnectionClosed, "control Stop wakes finite Prepare without waiting for cleanup");
            await ExpectCode(() => state.BindAsync(long.MaxValue, f.Hash), SharpLinkErrorCode.Unavailable,
                "control sealed binding state refuses new work");
            release.TrySetResult();
            await stop.WaitAsync(token);
            await disposal.WaitAsync(token);
            await ExpectCode(() => binding, SharpLinkErrorCode.Unavailable, "control stopped Bind cannot publish success");
            var snapshot = f.Client.FrameworkTaskSnapshotForDiagnostics;
            Check(state.PendingCount == 0 && state.CompletedCount == 0 && f.Client.BindingBudget.Bindings == 0 &&
                f.Client.BindingBudget.Operations == 0 && snapshot.IsDrained && snapshot.ActiveTasks == 0 &&
                snapshot.RejectedRegistrations == 0, "control Stop joins Bind cleanup and drains all quota owners");
        }
        finally { release.TrySetResult(); }
    }
}

