namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    [Test]
    public async Task StateCapacityMustWaitForTombstoneButRejectLiveExhaustion()
    {
        using var fixture = new Fixture(window: 4, connection: 4, maxStreams: 1);
        var first = fixture.Owner.Open(1, 0);
        await ExpectFailure(OpenAnother(fixture.Owner));
        await fixture.Owner.EnqueueAsync(first, new Packet(1), 1);
        await fixture.Owner.EnqueueAsync(first, new Packet(9), 0, terminal: true);
        Finish(fixture.Take());
        Finish(fixture.Take());
        var waiting = fixture.Owner.OpenAsync(2, 0, CancellationToken.None);
        Ensure(!waiting.IsCompleted, "tombstone pressure must wait for credit");
        fixture.Owner.PostWindowUpdate(1, 0, 1);
        fixture.PumpChanges();
        var second = await waiting.AsTask().WaitAsync(Limit);
        Ensure(second.RequestId == 2 && fixture.Owner.RetainedStreams == 1, "state capacity was not transferred");
        static async ValueTask OpenAnother(WriterOwnedStreamScheduler owner)
            => _ = await owner.OpenAsync(2, 0, CancellationToken.None);
    }

    [Test]
    public async Task CancelingStateCapacityWaitMustNotConsumeFutureCapacity()
    {
        using var fixture = new Fixture(window: 4, connection: 4, maxStreams: 1);
        var first = fixture.Owner.Open(1, 0);
        await fixture.Owner.EnqueueAsync(first, new Packet(1), 1);
        await fixture.Owner.EnqueueAsync(first, new Packet(9), 0, terminal: true);
        Finish(fixture.Take());
        Finish(fixture.Take());
        using var cancel = new CancellationTokenSource();
        var waiting = fixture.Owner.OpenAsync(2, 0, cancel.Token).AsTask();
        cancel.Cancel();
        await ExpectFailure(new ValueTask(waiting));
        fixture.Owner.PostWindowUpdate(1, 0, 1);
        fixture.PumpChanges();
        var next = await fixture.Owner.OpenAsync(3, 0, CancellationToken.None);
        Ensure(next.RequestId == 3, "canceled admission retained capacity");
    }

    [Test]
    public async Task RingCapacityWaitMustWakeOnlyAfterWriterTakesAFrame()
    {
        using var fixture = new Fixture(slots: 1);
        var stream = fixture.Owner.Open(1, 0);
        await fixture.Owner.EnqueueAsync(stream, new Packet(1), 1);
        var next = new Packet(2);
        var waiting = fixture.Owner.EnqueueAsync(stream, next, 1);
        Ensure(!waiting.IsCompleted && next.Returns == 0, "ring did not bound preparation");
        var rejected = new Packet(3);
        await ExpectFailure(fixture.Owner.EnqueueAsync(stream, rejected, 1));
        Ensure(rejected.Returns == 1, "concurrent producer packet leaked");
        Finish(fixture.Take());
        await waiting.AsTask().WaitAsync(Limit);
        Ensure(Marker(fixture.PeekAndFinish()) == 2, "waiting producer did not retain item order");
    }

    [Test]
    public async Task CancelingRingWaitMustReturnOnlyItsOwnPacket()
    {
        using var fixture = new Fixture(slots: 1);
        var stream = fixture.Owner.Open(1, 0);
        var first = new Packet(1);
        var second = new Packet(2);
        await fixture.Owner.EnqueueAsync(stream, first, 1);
        using var cancel = new CancellationTokenSource();
        var waiting = fixture.Owner.EnqueueAsync(stream, second, 1, cancellationToken: cancel.Token);
        cancel.Cancel();
        await ExpectFailure(waiting);
        Ensure(first.Returns == 0 && second.Returns == 1 && stream.ProducerActive == 0, "cancellation stole or retained ownership");
        Finish(fixture.Take());
    }

    [Test]
    public async Task GlobalPreparedBudgetAndItsWaiterEnvelopeMustStayBounded()
    {
        using var fixture = new Fixture(prepared: 48, maxPacket: 48);
        var a = fixture.Owner.Open(1, 0);
        var b = fixture.Owner.Open(2, 0);
        var c = fixture.Owner.Open(3, 0);
        var d = fixture.Owner.Open(4, 0);
        await fixture.Owner.EnqueueAsync(a, new Packet(1), 1);
        await fixture.Owner.EnqueueAsync(b, new Packet(2), 1);
        var waiting = fixture.Owner.EnqueueAsync(c, new Packet(3), 1);
        Ensure(!waiting.IsCompleted && fixture.Owner.PreparedBytes == 48, "prepared-byte bound was not enforced");
        var excess = new Packet(4);
        await ExpectFailure(fixture.Owner.EnqueueAsync(d, excess, 1));
        Ensure(excess.Returns == 1, "excess budget waiter retained its serialized packet");
        Finish(fixture.Take());
        await waiting.AsTask().WaitAsync(Limit);
        Ensure(fixture.Owner.PreparedBytes == 48, "released preparation was not transferred exactly");
    }

    [Test]
    public async Task AbortMustNotReturnWriterOwnedPacketAndMustNotReadReturnedBuffer()
    {
        using var fixture = new Fixture();
        var stream = fixture.Owner.Open(1, 0);
        var heldPacket = new Packet(1);
        var prepared = new Packet(2);
        await fixture.Owner.EnqueueAsync(stream, heldPacket, 1);
        var held = fixture.Take();
        await fixture.Owner.EnqueueAsync(stream, prepared, 1);
        fixture.Owner.Abort(stream, new IOException("local cancellation"));
        Ensure(prepared.Returns == 1 && heldPacket.Returns == 0, "abort crossed the pump ownership boundary");
        Ensure(fixture.Owner.PreparedBytes == 0, "buffer reuse corrupted prepared-byte release");
        fixture.PumpChanges();
        Ensure(fixture.Owner.ConnectionCredit == 63, "local abort refunded already admitted DATA");
        Finish(held);
        fixture.Owner.PostWindowUpdate(1, 0, 1);
        fixture.PumpChanges();
        Ensure(fixture.Owner.RetainedStreams == 0, "aborted stream never retired");
    }

    [Test]
    public async Task ConnectionCancellationMustWakePreparationWithoutStealingHeldOutput()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new Fixture(slots: 1, cancellation: cancel.Token);
        var stream = fixture.Owner.Open(1, 0);
        var heldPacket = new Packet(1);
        await fixture.Owner.EnqueueAsync(stream, heldPacket, 1);
        var held = fixture.Take();
        var buffered = new Packet(2);
        await fixture.Owner.EnqueueAsync(stream, buffered, 1);
        var blocked = new Packet(3);
        var waiting = fixture.Owner.EnqueueAsync(stream, blocked, 1);
        cancel.Cancel();
        await ExpectFailure(waiting);
        Ensure(buffered.Returns == 1 && blocked.Returns == 1 && heldPacket.Returns == 0, "stop stole output or leaked preparation");
        Finish(held);
        Ensure(fixture.Owner.PreparedBytes == 0, "stop leaked prepared bytes");
    }

    [Test]
    public async Task DrainSnapshotMustNotWaitForLaterPublications()
    {
        using var fixture = new Fixture();
        var stream = fixture.Owner.Open(1, 0);
        await fixture.Owner.EnqueueAsync(stream, new Packet(1), 1);
        var drain = fixture.Owner.DrainAcceptedFramesAsync(stream);
        await fixture.Owner.EnqueueAsync(stream, new Packet(2), 1);
        Finish(fixture.Take());
        await drain.WaitAsync(Limit);
        Ensure(stream.ReleasedFrames == 1 && stream.AcceptedFrames == 2, "drain followed future production");
        Finish(fixture.Take());
    }

    [Test]
    public async Task AbortingUnadmittedFramesMustFailTheirDrainWaiter()
    {
        using var fixture = new Fixture();
        var stream = fixture.Owner.Open(1, 0);
        await fixture.Owner.EnqueueAsync(stream, new Packet(1), 1);
        var drain = fixture.Owner.DrainAcceptedFramesAsync(stream);
        var error = new IOException("aborted before admission");
        fixture.Owner.Abort(stream, error);
        try { await drain.WaitAsync(Limit); throw new Exception("drain unexpectedly succeeded"); }
        catch (IOException observed) { Ensure(ReferenceEquals(observed, error), "drain lost terminal error identity"); }
    }

    [Test]
    public async Task OversizedFrameRejectionMustNotPoisonUnrelatedStreams()
    {
        using var fixture = new Fixture(maxPacket: 24);
        var first = fixture.Owner.Open(1, 0);
        var tooLarge = new Packet(1, bytes: 48);
        await ExpectFailure(fixture.Owner.EnqueueAsync(first, tooLarge, 1));
        Ensure(tooLarge.Returns == 1 && fixture.Owner.CleanupFailure is null, "call rejection poisoned connection");
        var second = fixture.Owner.Open(2, 0);
        await fixture.Owner.EnqueueAsync(second, new Packet(2), 1);
        Finish(fixture.Take());
    }
}
