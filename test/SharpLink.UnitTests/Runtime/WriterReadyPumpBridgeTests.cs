using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterReadyPumpBridgeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ReadyBufferMustRemainOwnedUntilFlushCompletes()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 0 && source.Releases == 0, "flush still owns frame");
        Ensure(pump.QueuedBytes == packet.WrittenCount, "queue reservation remains pinned");
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Releases == 1, "exactly one buffer/release");
        Ensure(pump.QueuedBytes == 0, "queue reservation returned");
        Ensure(pump.Output.Markers.SequenceEqual(new byte[] { 9 }), "exact bytes written");
    }

    [Test]
    public async Task StopCannotReturnAFrameFromABlockedFlush()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.Stop();
        Ensure(packet.Returns == 0 && source.Stops == 0, "stop does not steal writer ownership");
        pump.Output.ReleaseFirstFlush();
        await pump.Join().WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Stops == 1, "writer settles before source stop");
        Ensure(source.ReleasesAtStop == 1, "stop follows admitted-frame release");
    }

    [Test]
    public async Task FailedFlushMustReleaseFrameAndStopSource()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.Output.ReleaseFirstFlush(new IOException("injected output fault"));
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Releases == 1, "failure must settle once");
        Ensure(source.ReleaseError is not null && pump.QueuedBytes == 0, "failed release carries error");
    }

    [Test]
    public async Task LateAttachmentMustNotStopSourceOnAttachingThread()
    {
        await using var pump = new PumpFixture();
        pump.Stop();
        await pump.Join().WaitAsync(Limit);
        var source = new Source();
        try
        {
            pump.Attach(source);
            throw new Exception("late attach was accepted");
        }
        catch (SharpLinkException)
        {
        }
        Ensure(source.Stops == 0, "failed attachment did not transfer source ownership");
    }

    [Test]
    public async Task AttachmentRacingStopHasOneClearOwner()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using var pump = new PumpFixture();
            var source = new Source();
            using var start = new ManualResetEventSlim();
            var attach = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    pump.Attach(source);
                    return true;
                }
                catch (SharpLinkException)
                {
                    return false;
                }
            });
            var stop = Task.Run(() =>
            {
                start.Wait();
                pump.Stop();
            });
            start.Set();
            await stop.WaitAsync(Limit);
            var attached = await attach.WaitAsync(Limit);
            await pump.Join().WaitAsync(Limit);
            Ensure(source.Stops == (attached ? 1 : 0), "attachment/shutdown ownership mismatch");
        }
    }

    [Test]
    public async Task ContinuouslyQueuedOrdinaryFramesMustNotStarveReadyFrame()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        pump.EnqueueOrdinary(new Packet(1));
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        for (var index = 0; index < 128; index++)
            pump.EnqueueOrdinary(new Packet(1));
        var source = new Source(new Packet(9));
        pump.Attach(source);
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        var position = Array.IndexOf(pump.Output.Markers.ToArray(), (byte)9);
        Ensure(position >= 1 && position <= 17, "ready frame exceeded ordinary quantum");
    }

    [Test]
    public async Task FalseTakeAfterReservationMustNotLeakQueueBytes()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveThenFalse };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0, "false take leaked its reservation");
        Ensure(packet.Returns == 1 && source.Releases == 0, "source retained prepared ownership");
    }

    [Test]
    public async Task ThrowAfterReservationMustNotLeakQueueBytes()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveThenThrow };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "throw leaked reservation or packet");
    }

    [Test]
    public async Task DuplicateReservationMustBeRejectedAndRolledBack()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveTwice };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "duplicate reservation rollback failed");
    }

    [Test]
    public async Task LengthMismatchMustReturnTransferredPacketExactlyOnce()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.WrongLength };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "transferred invalid packet leaked");
        Ensure(source.Releases == 1 && source.ReleaseError is not null, "source lease not failed");
    }

    [Test]
    public async Task ReleaseFailureMustNotDoubleReleaseTheBatchOrSkipSourceStop()
    {
        await using var pump = new PumpFixture(lowLatency: false);
        var first = new Packet(7) { ThrowOnReturn = true };
        var second = new Packet(8);
        var source = new Source(first, second);
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        try
        {
            await pump.Join().WaitAsync(Limit);
        }
        catch (AggregateException)
        {
        }
        Ensure(first.Returns == 1 && second.Returns == 1, "batch was released twice or truncated");
        Ensure(source.Releases == 2 && source.Stops == 1, "final source settlement skipped");
        Ensure(pump.QueuedBytes == 0, "failed release corrupted queue accounting");
    }

    [Test]
    public async Task ContinuouslyReadyFramesMustNotStarveOrdinaryFrame()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var source = new Source(Enumerable.Range(0, 128).Select(_ => new Packet(9)).ToArray());
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.EnqueueOrdinary(new Packet(7));
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        var position = Array.IndexOf(pump.Output.Markers.ToArray(), (byte)7);
        Ensure(position >= 1 && position <= 16, "ordinary frame exceeded ready quantum");
    }

    [Test]
    public async Task AttachedSourceMustPreserveExplicitTimedBatchPolicy()
    {
        var clock = new ManualClock();
        await using var pump = new PumpFixture(clock: clock,
            flushOptions: new RpcSessionFlushOptions(64 * 1024, TimeSpan.FromSeconds(1)));
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await clock.TimerArmed.Task.WaitAsync(Limit);
        Ensure(!pump.Output.FirstFlushEntered.Task.IsCompleted, "source bypassed timed batching");
        Ensure(packet.Returns == 0, "pending batch lost its buffer");
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.Released.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 1 && pump.QueuedBytes == 0, "timed batch did not settle");
    }

    [Test]
    public async Task ForceFlushInReadyLaneMustPreservePrecedingDataOrder()
    {
        var clock = new ManualClock();
        await using var pump = new PumpFixture(clock: clock,
            flushOptions: new RpcSessionFlushOptions(64 * 1024, TimeSpan.FromSeconds(1)));
        var source = new Source(new Packet(1), new Packet(9)) { ForceFlushLast = true };
        pump.Attach(source);
        await source.Released.Task.WaitAsync(Limit);
        Ensure(pump.Output.Markers.SequenceEqual(new byte[] { 1, 9 }), "ready lane reordered data/terminal marker");
        Ensure(!clock.TimerArmed.Task.IsCompleted, "force flush waited for batch deadline");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
