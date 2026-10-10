namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterFlowSchedulerTests
{
    [Test]
    public async Task ExactSizeReservationMustPrecedeMaterializationAndNotDebitCredit()
    {
        await using var f = new Fixture(preparedBytes: 48);
        var a = await f.Open(1);
        var b = await f.Open(2);
        var first = await f.Scheduler.ReservePreparationAsync(a, 48);
        var second = f.Scheduler.ReservePreparationAsync(b, 24).AsTask();
        Ensure(!second.IsCompleted && f.Scheduler.PreparedBytes == 48, "serializer could bypass memory admission");
        Ensure(f.Scheduler.ConnectionCredit == 4 && a.Frames.Count == 0, "memory admission changed wire credit");
        first.Dispose();
        using var reserved = await second.WaitAsync(Limit);
        Ensure(f.Scheduler.PreparedBytes == 24, "memory admission did not transfer exact bytes");
    }

    [Test]
    public async Task ShorterCompressedPacketMustReleaseItsFullRawReservation()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        var reservation = await f.Scheduler.ReservePreparationAsync(stream, 48);
        await f.Scheduler.PublishPreparedAsync(reservation, new Packet(1, 24), 4);
        reservation.Dispose();
        Ensure(f.Scheduler.PreparedBytes == 48, "publication discarded raw preparation ownership");
        f.Release(f.Take(1));
        Ensure(f.Scheduler.PreparedBytes == 0, "compressed handoff leaked reserved bytes");
        f.AssertCredit();
    }

    [Test]
    public async Task DisposedPreparationMustNotReleaseTheNextProducerReservation()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        var old = await f.Scheduler.ReservePreparationAsync(stream, 24);
        old.Dispose();
        using var next = await f.Scheduler.ReservePreparationAsync(stream, 48);
        old.Dispose();
        Ensure(f.Scheduler.PreparedBytes == 48 && stream.ProducerBusy, "stale preparation changed the new owner");
    }

    [Test]
    public async Task CanceledPreparationReservationMustReleaseProducerOwnership()
    {
        await using var f = new Fixture(preparedBytes: 48);
        var a = await f.Open(1);
        var b = await f.Open(2);
        using var first = await f.Scheduler.ReservePreparationAsync(a, 48);
        using var cancellation = new CancellationTokenSource();
        var waiting = f.Scheduler.ReservePreparationAsync(b, 24, cancellation.Token).AsTask();
        Ensure(!waiting.IsCompleted, "reservation did not suspend");
        cancellation.Cancel();
        await Expect<OperationCanceledException>(async () => { _ = await waiting.WaitAsync(Limit); });
        Ensure(!b.ProducerBusy && f.Scheduler.PreparedBytes == 48, "canceled reservation leaked its ownership");
    }

    [Test]
    public async Task ShutdownMustJoinAProducerThatHasNotPublishedItsReservation()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        var reservation = await f.Scheduler.ReservePreparationAsync(stream, 24);
        f.Scheduler.Stopped(new IOException("shutdown"));
        Ensure(!f.Scheduler.StoppedTask.IsCompleted, "shutdown did not join active preparation");
        reservation.Dispose();
        await Expect<IOException>(async () => await f.Scheduler.StoppedTask.WaitAsync(Limit));
        Ensure(f.Scheduler.PreparedBytes == 0, "joined preparation retained bytes");
    }

    [Test]
    public async Task FinalDrainMustWaitForHandoffButNotForTransportRelease()
    {
        await using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var stream = await f.Open(1, cancellation.Token);
        var packet = new Packet(1);
        await f.Scheduler.EnqueueAsync(stream, packet, 4);
        var drain = f.Scheduler.WaitForPreparedDrainAsync(stream, CancellationToken.None, finishProduction: true).AsTask();
        Ensure(!drain.IsCompleted, "terminal could overtake prepared DATA");
        var frame = f.Take(1);
        await drain.WaitAsync(Limit);
        Ensure(packet.Returns == 0 && stream.WriterPins == 1, "drain stole transport ownership");
        f.Scheduler.PrepareOrdinaryTerminal(stream);
        f.Scheduler.CommitOrdinaryTerminal(stream);
        cancellation.Cancel();
        Ensure(stream.AbortError is null, "normal producer disposal was treated as a new abort");
        f.Release(frame);
        f.Scheduler.PostWindowUpdate(1, 0, 4);
        f.PollEmpty();
        Ensure(f.Scheduler.RetainedStreams == 0, "normal terminal lifetime not reclaimed");
    }

    [Test]
    public async Task FinalDrainMustRejectSubsequentDataPublication()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        await f.Scheduler.WaitForPreparedDrainAsync(stream, CancellationToken.None, finishProduction: true);
        var late = new Packet(1);
        await Expect<SharpLinkException>(() => f.Scheduler.EnqueueAsync(stream, late, 1));
        Ensure(late.Returns == 1 && f.Scheduler.PreparedBytes == 0, "late DATA escaped final drain");
    }

    [Test]
    public async Task CallCancellationMustBreakFinalDrainBeforeTerminalCommit()
    {
        await using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var stream = await f.Open(1, cancellation.Token);
        var packet = new Packet(1);
        await f.Scheduler.EnqueueAsync(stream, packet, 4);
        var drain = f.Scheduler.WaitForPreparedDrainAsync(stream, CancellationToken.None, finishProduction: true).AsTask();
        cancellation.Cancel();
        await Expect<OperationCanceledException>(async () => await drain.WaitAsync(Limit));
        f.PollEmpty();
        Ensure(packet.Returns == 1 && f.Scheduler.RetainedStreams == 0, "canceled final drain kept prepared ownership");
    }

    [Test]
    public async Task OrdinaryTerminalMustRejectUndrainedPreparedData()
    {
        await using var f = new Fixture();
        var stream = await f.Open(1);
        await f.Scheduler.EnqueueAsync(stream, new Packet(1), 1);
        await Expect<InvalidOperationException>(() =>
        {
            f.Scheduler.PrepareOrdinaryTerminal(stream);
            return ValueTask.CompletedTask;
        });
        f.Release(f.Take(1));
        await f.Scheduler.WaitForPreparedDrainAsync(stream, CancellationToken.None, finishProduction: true);
        f.Scheduler.PrepareOrdinaryTerminal(stream);
        f.Scheduler.CommitOrdinaryTerminal(stream);
        f.Scheduler.PostWindowUpdate(1, 0, 1);
        f.PollEmpty();
        Ensure(f.Scheduler.RetainedStreams == 0, "ordered terminal did not retire");
    }
}
