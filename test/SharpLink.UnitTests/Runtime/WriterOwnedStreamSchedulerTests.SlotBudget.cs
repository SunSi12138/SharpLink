namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    [Test]
    public async Task PacketWaitingForAStreamSlotMustAlreadyOwnPreparationBudget()
    {
        using var fixture = new Fixture(slots: 1, prepared: 48, maxPacket: 24);
        using var cancellation = new CancellationTokenSource();
        var stream = fixture.Owner.Open(1, 0);
        var first = new Packet(1);
        var second = new Packet(2);
        await fixture.Owner.EnqueueAsync(stream, first, 1);
        var pending = fixture.Owner.EnqueueAsync(stream, second, 1,
            cancellationToken: cancellation.Token).AsTask();
        try
        {
            // The fixture has no autonomous writer. This is an actual suspended Enqueue,
            // not a timing-based observation: one packet is queued and a second serialized
            // packet is retained while waiting for that stream's sole queue slot.
            Ensure(!pending.IsCompleted, "fixture did not wait for its full stream slot");
            Ensure(fixture.Owner.PreparedBytes == 48,
                "slot-waiting serialized packet bypassed the connection preparation budget");
            cancellation.Cancel();
            try { await pending.WaitAsync(Limit); }
            catch (OperationCanceledException) { }
            Ensure(second.Returns == 1 && first.Returns == 0 && fixture.Owner.PreparedBytes == 24,
                "canceling slot wait did not release exactly its own packet and budget");
            Finish(fixture.Take());
            Ensure(fixture.Owner.PreparedBytes == 0, "queued predecessor did not release its budget");
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(Limit); }
            catch (OperationCanceledException) { }
        }
    }
}
