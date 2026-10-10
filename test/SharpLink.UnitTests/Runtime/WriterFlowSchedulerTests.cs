namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterFlowSchedulerTests
{
    [Test]
    public async Task OnlyWriterAdmissionMayDebitProtocolCredit()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        var packet = new Packet(1);
        await f.Scheduler.EnqueueAsync(stream, packet, 4);
        Ensure(f.Scheduler.ConnectionCredit == 4 && stream.Credit == 4, "producer debited credit");
        Ensure(f.Scheduler.PreparedBytes == 24, "prepared ownership not accounted");
        var frame = f.Take(1);
        Ensure(f.Scheduler.ConnectionCredit == 0 && stream.Credit == 0, "writer did not debit once");
        Ensure(f.Scheduler.PreparedBytes == 0 && packet.Returns == 0, "handoff did not transfer budget");
        f.Scheduler.PostWindowUpdate(1, 0, 4);
        Ensure(f.Scheduler.ConnectionCredit == 0, "reader mutated writer credit");
        f.PollEmpty();
        Ensure(f.Scheduler.ConnectionCredit == 4 && stream.WriterPins == 1, "early credit released writer pin");
        f.Release(frame);
        f.AssertCredit();
    }

    [Test]
    public async Task QueuePressureMustNotDebitOrDequeue()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        await f.Scheduler.EnqueueAsync(stream, new Packet(1), 4);
        Ensure(!f.Scheduler.TryTake(new Admission(false), out _), "rejected queue admission emitted");
        Ensure(stream.Frames.Count == 1 && f.Scheduler.PreparedBytes == 24, "probe lost preparation");
        Ensure(f.Scheduler.ConnectionCredit == 4 && stream.Credit == 4, "probe debited credit");
        f.Release(f.Take(1));
        f.AssertCredit();
    }

    [Test]
    public async Task StreamBlockedHeadMustNotStrandAnotherEligibleStream()
    {
        await using var f = new Fixture(streamWindow: 2, connectionWindow: 4);
        var a = await f.Open(1);
        var b = await f.Open(2);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 2);
        f.Release(f.Take(1));
        await f.Scheduler.EnqueueAsync(a, new Packet(2), 1);
        await f.Scheduler.EnqueueAsync(b, new Packet(3), 1);
        f.Release(f.Take(3));
        Ensure(a.Frames.Count == 1, "stream-blocked head was emitted");
        f.Scheduler.PostWindowUpdate(1, 0, 2);
        f.Release(f.Take(2));
        f.AssertCredit();
    }

    [Test]
    public async Task ConnectionBlockedHeadMustNotBeBypassedBySmallerData()
    {
        await using var f = new Fixture();
        var a = await f.Open(1);
        var b = await f.Open(2);
        var c = await f.Open(3);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 4);
        f.Release(f.Take(1));
        await f.Scheduler.EnqueueAsync(b, new Packet(2), 2);
        await f.Scheduler.EnqueueAsync(c, new Packet(3), 1);
        f.Scheduler.PostWindowUpdate(1, 0, 1);
        f.PollEmpty();
        Ensure(f.Scheduler.ConnectionCredit == 1, "later smaller DATA bypassed connection head");
        f.Scheduler.PostWindowUpdate(1, 0, 1);
        f.Release(f.Take(2));
        f.PollEmpty();
        f.Scheduler.PostWindowUpdate(2, 0, 1);
        f.Release(f.Take(3));
        f.AssertCredit();
    }

    [Test]
    public async Task OversizedItemMayBorrowOnceAndRepayExactly()
    {
        await using var f = new Fixture(streamWindow: 2, connectionWindow: 4);
        var stream = await f.Open(1);
        await f.Scheduler.EnqueueAsync(stream, new Packet(1), 6);
        f.Release(f.Take(1));
        Ensure(stream.Credit == -4 && f.Scheduler.ConnectionCredit == -2, "oversized loan changed");
        await f.Scheduler.EnqueueAsync(stream, new Packet(2), 1);
        f.PollEmpty();
        f.Scheduler.PostWindowUpdate(1, 0, 6);
        f.Release(f.Take(2));
        Ensure(stream.Credit == 1 && f.Scheduler.ConnectionCredit == 3, "loan repayment was not exact");
        f.AssertCredit();
    }

    [Test]
    public async Task LateCanceledRefundMustNotCreateAnotherStreamsCredit()
    {
        await using var f = new Fixture();
        var a = await f.Open(1);
        var b = await f.Open(2);
        var c = await f.Open(3);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 4);
        var held = f.Take(1);
        f.Scheduler.Abort(a, new OperationCanceledException());
        f.PollEmpty();
        await f.Scheduler.EnqueueAsync(b, new Packet(2), 4);
        f.Release(f.Take(2));
        f.Scheduler.PostWindowUpdate(1, 0, 4);
        await f.Scheduler.EnqueueAsync(c, new Packet(3), 1);
        f.PollEmpty();
        Ensure(f.Scheduler.ConnectionCredit == 0 && a.WriterPins == 1, "late canceled refund fabricated credit");
        f.Release(held);
        f.Scheduler.PostWindowUpdate(2, 0, 4);
        f.Release(f.Take(3));
        f.AssertCredit();
    }

    [Test]
    public async Task TerminalMustFollowDataButNeedNoFlowCredit()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        await f.Scheduler.EnqueueAsync(stream, new Packet(1), 4);
        await f.Scheduler.SealAsync(stream, new Packet(9));
        var data = f.Take(1);
        var terminal = f.Take(9);
        Ensure(f.Scheduler.ConnectionCredit == 0 && stream.WriterPins == 2, "terminal consumed protocol credit");
        f.Scheduler.PostWindowUpdate(1, 0, 4);
        f.PollEmpty();
        Ensure(f.Scheduler.RetainedStreams == 1, "early wire refund released writer lifetime");
        f.Release(data);
        Ensure(f.Scheduler.RetainedStreams == 1, "DATA release lost terminal pin");
        f.Release(terminal);
        Ensure(f.Scheduler.RetainedStreams == 0, "settled tombstone not reclaimed");
        f.AssertCredit();
    }

    [Test]
    public async Task OldLeaseCannotPublishOrAbortAReplacement()
    {
        await using var f = new Fixture();
        var old = await f.Open(1);
        await f.Scheduler.SealAsync(old, new Packet(9));
        f.Release(f.Take(9));
        var replacement = await f.Open(1);
        Ensure(replacement.Generation != old.Generation, "same generation reused");
        var invalid = new Packet(2);
        await Expect<SharpLinkException>(() => f.Scheduler.EnqueueAsync(old, invalid, 1));
        f.Scheduler.Abort(old, new OperationCanceledException());
        Ensure(invalid.Returns == 1 && replacement.AbortError is null, "old lease changed replacement");
        await f.Scheduler.EnqueueAsync(replacement, new Packet(3), 1);
        f.Release(f.Take(3));
        f.AssertCredit();
    }

    [Test]
    public async Task DuplicateAndExcessUpdatesRemainCoupledAcrossStreams()
    {
        await using var f = new Fixture();
        var a = await f.Open(1);
        var b = await f.Open(2);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 4);
        f.Release(f.Take(1));
        f.Scheduler.PostWindowUpdate(1, 0, int.MaxValue);
        f.Scheduler.PostWindowUpdate(1, 0, int.MaxValue);
        f.PollEmpty();
        await f.Scheduler.EnqueueAsync(b, new Packet(2), 4);
        f.Release(f.Take(2));
        f.Scheduler.PostWindowUpdate(1, 0, int.MaxValue);
        f.PollEmpty();
        Ensure(f.Scheduler.ConnectionCredit == 0, "clamped stream update increased connection credit");
        f.AssertCredit();
    }

    [Test]
    public async Task UnpublishedRequestFenceMustBlockItsDataOnly()
    {
        await using var f = new Fixture();
        var a = await f.Open(1, startAllowed: false);
        var b = await f.Open(2);
        await f.Scheduler.EnqueueAsync(a, new Packet(1), 1);
        await f.Scheduler.EnqueueAsync(b, new Packet(2), 1);
        f.Release(f.Take(2));
        f.PollEmpty();
        f.Scheduler.AllowStart(a);
        f.Release(f.Take(1));
        f.AssertCredit();
    }

    [Test]
    public async Task ReadyQuantumMustBoundConsecutiveStreamFrames()
    {
        await using var f = new Fixture(streamWindow: 16, connectionWindow: 32, quantum: 2);
        var a = await f.Open(1);
        var b = await f.Open(2);
        for (var index = 0; index < 4; index++)
        {
            await f.Scheduler.EnqueueAsync(a, new Packet(1), 1);
            await f.Scheduler.EnqueueAsync(b, new Packet(2), 1);
        }
        foreach (var marker in new byte[] { 1, 1, 2, 2, 1, 1, 2, 2 })
            f.Release(f.Take(marker));
        f.AssertCredit();
    }
}
