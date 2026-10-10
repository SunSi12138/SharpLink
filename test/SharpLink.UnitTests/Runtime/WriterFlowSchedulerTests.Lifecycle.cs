namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterFlowSchedulerTests
{
    [Test]
    public async Task HardActiveLimitMustRejectNewStream()
    {
        await using var f = new Fixture(maxStreams: 1);
        _ = await f.Open(1);
        await Expect<SharpLinkException>(async () => { _ = await f.Open(2); });
        Ensure(f.Scheduler.ActiveStreams == 1 && f.Scheduler.RetainedStreams == 1, "capacity rejection leaked a state");
    }

    [Test]
    public async Task TombstoneCapacityMustWaitForBothWireCreditAndWriterRelease()
    {
        await using var f = new Fixture(maxStreams: 1);
        var old = await f.Open(1);
        await f.Scheduler.EnqueueAsync(old, new Packet(1), 4);
        await f.Scheduler.SealAsync(old, new Packet(9));
        var data = f.Take(1);
        var terminal = f.Take(9);
        var next = f.Open(2).AsTask();
        Ensure(!next.IsCompleted, "tombstone capacity was not retained");
        await Expect<SharpLinkException>(async () => { _ = await f.Open(3); });
        f.Scheduler.PostWindowUpdate(1, 0, 4);
        f.PollEmpty();
        Ensure(!next.IsCompleted, "credit returned before writer allowed reuse");
        f.Release(data);
        Ensure(!next.IsCompleted, "terminal still owns its packet");
        f.Release(terminal);
        _ = await next.WaitAsync(Limit);
        Ensure(f.Scheduler.ActiveStreams == 1 && f.Scheduler.RetainedStreams == 1, "capacity waiter not admitted once");
    }

    [Test]
    public async Task CanceledCapacityWaiterMustNotPublishAnOwnerlessStream()
    {
        await using var f = new Fixture(maxStreams: 1);
        var old = await f.Open(1);
        await f.Scheduler.SealAsync(old, new Packet(9));
        var terminal = f.Take(9);
        using var cancellation = new CancellationTokenSource();
        var waiting = f.Open(2, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Expect<OperationCanceledException>(async () => { _ = await waiting.WaitAsync(Limit); });
        f.Release(terminal);
        _ = await f.Open(3);
        Ensure(f.Scheduler.RetainedStreams == 1, "canceled open leaked an identity");
    }

    [Test]
    public async Task GlobalPreparedBudgetMustRemainBoundedBeforeCredit()
    {
        await using var f = new Fixture(preparedBytes: 48);
        var a = await f.Open(1);
        var b = await f.Open(2);
        var c = await f.Open(3);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 1);
        await f.Scheduler.EnqueueAsync(b, new Packet(2), 1);
        var waiting = f.Scheduler.EnqueueAsync(c, new Packet(3), 1).AsTask();
        Ensure(!waiting.IsCompleted && f.Scheduler.PreparedBytes == 48, "prepared budget was bypassed");
        f.Release(f.Take(1));
        await waiting.WaitAsync(Limit);
        Ensure(f.Scheduler.PreparedBytes == 48, "budget transfer did not admit exact bytes");
        f.Release(f.Take(2));
        f.Release(f.Take(3));
        Ensure(f.Scheduler.PreparedBytes == 0, "prepared bytes leaked");
        f.AssertCredit();
    }

    [Test]
    public async Task AbortMustWakeCapacityWaitAndReturnEachPreparedPacketOnce()
    {
        await using var f = new Fixture(slots: 1);
        var stream = await f.Open(1);
        var first = new Packet(1);
        var second = new Packet(2);
        var third = new Packet(3);
        await f.Scheduler.EnqueueAsync(stream, first, 1);
        var waiting = f.Scheduler.EnqueueAsync(stream, second, 1).AsTask();
        Ensure(!waiting.IsCompleted, "full ring did not suspend producer");
        await Expect<InvalidOperationException>(() => f.Scheduler.EnqueueAsync(stream, third, 1));
        f.Scheduler.Abort(stream, new OperationCanceledException());
        await Expect<OperationCanceledException>(async () => await waiting.WaitAsync(Limit));
        f.PollEmpty();
        Ensure(first.Returns == 1 && second.Returns == 1 && third.Returns == 1, "prepared cleanup was not exact");
        Ensure(f.Scheduler.PreparedBytes == 0 && f.Scheduler.RetainedStreams == 0, "canceled ring retained resources");
    }

    [Test]
    public async Task CallCancellationMustNotStealAdmittedPacket()
    {
        await using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var stream = await f.Open(1, cancellation.Token);
        var admitted = new Packet(1);
        var prepared = new Packet(2);
        await f.Scheduler.EnqueueAsync(stream, admitted, 4);
        var frame = f.Take(1);
        await f.Scheduler.EnqueueAsync(stream, prepared, 1);
        cancellation.Cancel();
        Ensure(prepared.Returns == 1 && admitted.Returns == 0, "cancellation stole admitted ownership");
        Ensure(f.Scheduler.PreparedBytes == 0 && f.Scheduler.ConnectionCredit == 0, "callback changed writer credit");
        f.PollEmpty();
        Ensure(f.Scheduler.ConnectionCredit == 4 && f.Scheduler.RetainedStreams == 1, "abort lost writer pin");
        f.Release(frame);
        Ensure(f.Scheduler.RetainedStreams == 0, "aborted lifetime not retired after release");
        f.AssertCredit();
    }

    [Test]
    public async Task CleanupMustPinSameKeyUntilBudgetRetirementFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var f = new Fixture(returnPacket: packet =>
        {
            entered.Set();
            Ensure(release.Wait(Limit), "cleanup barrier timed out");
            packet.Dispose();
        });
        var old = await f.Open(1);
        await f.Scheduler.EnqueueAsync(old, new Packet(1), 1);
        var abort = Task.Run(() => f.Scheduler.Abort(old, new OperationCanceledException()));
        try
        {
            Ensure(entered.Wait(Limit), "abort did not enter pool return");
            f.PollEmpty();
            await Expect<SharpLinkException>(async () => { _ = await f.Open(1); });
            Ensure(f.Scheduler.RetainedStreams == 1, "cleanup lost same-key pin");
        }
        finally
        {
            release.Set();
        }
        await abort.WaitAsync(Limit);
        f.PollEmpty();
        _ = await f.Open(1);
    }

    [Test]
    public async Task StopCompletionMustJoinConcurrentPreparedCleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var f = new Fixture(returnPacket: packet =>
        {
            entered.Set();
            Ensure(release.Wait(Limit), "cleanup barrier timed out");
            packet.Dispose();
        });
        var stream = await f.Open(1);
        await f.Scheduler.EnqueueAsync(stream, new Packet(1), 1);
        var abort = Task.Run(() => f.Scheduler.Abort(stream, new OperationCanceledException()));
        try
        {
            Ensure(entered.Wait(Limit), "abort did not enter pool return");
            f.Scheduler.Stopped(new IOException("stop"));
            Ensure(!f.Scheduler.StoppedTask.IsCompleted, "stop completed before packet cleanup");
        }
        finally
        {
            release.Set();
        }
        await abort.WaitAsync(Limit);
        await Expect<IOException>(async () => await f.Scheduler.StoppedTask.WaitAsync(Limit));
        Ensure(f.Scheduler.PreparedBytes == 0, "shutdown leaked preparation bytes");
    }

    [Test]
    public async Task ForeignLeaseAndImpossibleFrameMustFailBeforeWriterAdmission()
    {
        await using var f = new Fixture();
        await using var other = new Fixture();
        var foreign = await other.Open(1);
        var packet = new Packet(1);
        await Expect<InvalidOperationException>(() => f.Scheduler.EnqueueAsync(foreign, packet, 1));
        Ensure(packet.Returns == 1, "foreign-handle failure leaked packet");
        var own = await f.Open(2);
        var oversized = new Packet(2, 2048);
        await Expect<SharpLinkException>(() => f.Scheduler.EnqueueAsync(own, oversized, 1));
        Ensure(oversized.Returns == 1 && f.Scheduler.ConnectionCredit == 4, "impossible frame reached writer");
    }
}
