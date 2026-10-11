namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    [Test]
    public async Task PreparationAndRejectedPumpAdmissionMustNotDebitWireCredit()
    {
        using var fixture = new Fixture(window: 4, connection: 4);
        var stream = fixture.Owner.Open(1, 0);
        var packet = new Packet(1);
        await fixture.Owner.EnqueueAsync(stream, packet, 4);
        Ensure(fixture.Owner.ConnectionCredit == 4 && fixture.Owner.PreparedBytes == 24, "preparation is not wire permission");
        Ensure(!fixture.Owner.TryTake(new Admission(false), out _), "denied queue admission must retain preparation");
        Ensure(fixture.Owner.ConnectionCredit == 4 && packet.Returns == 0, "rejected admission debited or returned a frame");
        var frame = fixture.Take();
        Ensure(fixture.Owner.ConnectionCredit == 0 && fixture.Owner.PreparedBytes == 0, "writer must own the exact debit");
        Finish(frame);
        fixture.Owner.PostWindowUpdate(1, 0, 4);
        fixture.PumpChanges();
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task TerminalMustFollowItsOwnDataWithoutConsumingCredit()
    {
        using var fixture = new Fixture(window: 2, connection: 2);
        var stream = fixture.Owner.Open(1, 0);
        await fixture.Owner.EnqueueAsync(stream, new Packet(1), 2);
        await fixture.Owner.EnqueueAsync(stream, new Packet(9), 0, terminal: true);
        var data = fixture.Take();
        Ensure(Marker(data) == 1, "terminal overtook DATA");
        var terminal = fixture.Take();
        Ensure(Marker(terminal) == 9 && terminal.CreditBytes == 0, "terminal debited credit");
        Finish(data);
        Finish(terminal);
        Ensure(fixture.Owner.RetainedStreams == 1, "outstanding wire debt must retain tombstone");
        fixture.Owner.PostWindowUpdate(1, 0, 2);
        fixture.PumpChanges();
        Ensure(fixture.Owner.RetainedStreams == 0, "final credit did not retire tombstone");
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task ConnectionBlockedDataMustNotStrandAnotherStreamsEof()
    {
        using var fixture = new Fixture(window: 4, connection: 4);
        var first = fixture.Owner.Open(1, 0);
        var second = fixture.Owner.Open(2, 0);
        await fixture.Owner.EnqueueAsync(first, new Packet(1), 1);
        Finish(fixture.Take());
        // Only three connection bytes remain; the peer may batch the first byte until EOF.
        await fixture.Owner.EnqueueAsync(second, new Packet(2), 4);
        await fixture.Owner.EnqueueAsync(first, new Packet(9), 0, terminal: true);
        var eof = fixture.Take();
        Ensure(Marker(eof) == 9, "credit-free EOF was stranded behind a connection-credit waiter");
        Finish(eof);
        Ensure(!fixture.Owner.TryTake(new Admission(true), out _), "blocked DATA was admitted early");
        fixture.Owner.PostWindowUpdate(1, 0, 1);
        var data = fixture.Take();
        Ensure(Marker(data) == 2, "EOF's returned credit did not unblock DATA");
        Finish(data);
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task LaterSmallerDataMustNotBypassConnectionBlockedHead()
    {
        using var fixture = new Fixture(window: 4, connection: 4);
        var owner = fixture.Owner.Open(1, 0);
        var head = fixture.Owner.Open(2, 0);
        var later = fixture.Owner.Open(3, 0);
        await fixture.Owner.EnqueueAsync(owner, new Packet(1), 3);
        Finish(fixture.Take());
        await fixture.Owner.EnqueueAsync(head, new Packet(2), 2);
        await fixture.Owner.EnqueueAsync(later, new Packet(3), 1);
        Ensure(!fixture.Owner.TryTake(new Admission(true), out _), "later smaller DATA bypassed head");
        fixture.Owner.PostWindowUpdate(1, 0, 2);
        var first = fixture.Take();
        Ensure(Marker(first) == 2, "connection waiter order changed");
        Finish(first);
        var second = fixture.Take();
        Ensure(Marker(second) == 3, "eligible later stream did not progress");
        Finish(second);
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task StreamLocalBlockedHeadMustAllowAnEligibleStream()
    {
        using var fixture = new Fixture(window: 2, connection: 4);
        var head = fixture.Owner.Open(1, 0);
        var next = fixture.Owner.Open(2, 0);
        await fixture.Owner.EnqueueAsync(head, new Packet(1), 2);
        Finish(fixture.Take());
        await fixture.Owner.EnqueueAsync(head, new Packet(2), 1);
        await fixture.Owner.EnqueueAsync(next, new Packet(3), 1);
        var frame = fixture.Take();
        Ensure(Marker(frame) == 3, "stream-only pressure blocked independent progress");
        Finish(frame);
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task OversizedBorrowMustRepayExactlyBeforeAnotherAdmission()
    {
        using var fixture = new Fixture(window: 2, connection: 4);
        var first = fixture.Owner.Open(1, 0);
        var second = fixture.Owner.Open(2, 0);
        await fixture.Owner.EnqueueAsync(first, new Packet(1), 6);
        Finish(fixture.Take());
        Ensure(first.Credit == -4 && fixture.Owner.ConnectionCredit == -2, "oversized debt differs");
        await fixture.Owner.EnqueueAsync(second, new Packet(2), 1);
        Ensure(!fixture.Owner.TryTake(new Admission(true), out _), "oversized debt was borrowed twice");
        fixture.Owner.PostWindowUpdate(1, 0, 6);
        Finish(fixture.Take());
        Ensure(fixture.Owner.ConnectionCredit == 3, "oversized repayment was not exact");
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task UnknownAndPreAdmissionUpdatesMustNotCreatePermission()
    {
        using var fixture = new Fixture(window: 4, connection: 4);
        fixture.Owner.PostWindowUpdate(404, 7, 100);
        var stream = fixture.Owner.Open(1, 0);
        fixture.Owner.PostWindowUpdate(1, 0, 100);
        fixture.PumpChanges();
        await fixture.Owner.EnqueueAsync(stream, new Packet(1), 4);
        Finish(fixture.Take());
        Ensure(fixture.Owner.ConnectionCredit == 0, "pre-admission update manufactured permission");
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task LateAbortUpdateMustNotRefundAnotherStreamsDebt()
    {
        using var fixture = new Fixture(window: 4, connection: 4);
        var first = fixture.Owner.Open(1, 0);
        var second = fixture.Owner.Open(2, 0);
        await fixture.Owner.EnqueueAsync(first, new Packet(1), 4);
        var held = fixture.Take();
        fixture.Owner.AbortRequest(1, new IOException("remote cancel"));
        fixture.PumpChanges();
        Ensure(fixture.Owner.ConnectionCredit == 4 && first.WriterReferences == 1, "remote refund lost writer pin");
        await fixture.Owner.EnqueueAsync(second, new Packet(2), 4);
        Finish(fixture.Take());
        fixture.Owner.PostWindowUpdate(1, 0, 4);
        fixture.PumpChanges();
        Ensure(fixture.Owner.ConnectionCredit == 0, "late canceled-stream update refunded another stream");
        Finish(held);
        fixture.Owner.AssertCreditConservation();
    }

    [Test]
    public async Task OldHandleMustNotPublishIntoReopenedSameKey()
    {
        using var fixture = new Fixture();
        var old = fixture.Owner.Open(1, 7);
        await fixture.Owner.EnqueueAsync(old, new Packet(9), 0, terminal: true);
        Finish(fixture.Take());
        var current = fixture.Owner.Open(1, 7);
        Ensure(current.Generation != old.Generation && !ReferenceEquals(current, old), "lifecycle identity reused");
        var stale = new Packet(1);
        await ExpectFailure(fixture.Owner.EnqueueAsync(old, stale, 1));
        Ensure(stale.Returns == 1 && current.AcceptedFrames == 0, "old handle reached replacement");
        await fixture.Owner.EnqueueAsync(current, new Packet(2), 1);
        Finish(fixture.Take());
    }

    [Test]
    public async Task ForeignHandleMustReturnPacketWithoutChangingEitherLedger()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var handle = first.Owner.Open(1, 0);
        var packet = new Packet(1);
        await ExpectFailure(second.Owner.EnqueueAsync(handle, packet, 1));
        Ensure(packet.Returns == 1 && first.Owner.PreparedBytes == 0 && second.Owner.PreparedBytes == 0, "foreign handle changed ownership");
        first.Owner.AssertCreditConservation();
        second.Owner.AssertCreditConservation();
    }
}
