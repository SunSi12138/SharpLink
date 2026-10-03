namespace SharpLink.UnitTests.Client;

public sealed class EmissionEntryBarrierTests
{
    [Test]
    public async Task QuietPeriodAfterFlushDoesNotProveOutputWasObserved()
    {
        var observe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new TestTransportConnection(observe.Task);
        using var packet = new PooledByteBufferWriter();
        using (packet.BeginPacketScope(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0))
        { packet.GetSpan(sizeof(long))[..sizeof(long)].Clear(); packet.Advance(sizeof(long)); }
        await transport.Output.WriteAsync(packet.WrittenMemory);
        // The formerly used quiet-period drain returns empty deterministically,
        // even though a real Ping is already flushed into the transport pipe.
        var quiet = await transport.TryReadNextSentFrameAsync(TimeSpan.FromMilliseconds(20));
        if (quiet is not null) throw new Exception("Observer gate was bypassed.");
        var fence = transport.WaitForSentPacket(ProtocolV2FrameType.Ping);
        if (fence.IsCompleted) throw new Exception("Observation fence completed before actual parsing.");
        observe.TrySetResult();
        var ping = await fence.WaitAsync(TimeSpan.FromSeconds(5));
        if (ping.Type != ProtocolV2FrameType.Ping) throw new Exception("The previously flushed Ping was lost.");
    }

    [Test]
    public async Task DisposingBarrierAlwaysUnparksTheTransportWriter()
    {
        await using var transport = new TestTransportConnection();
        using var barrier = new EmissionEntryBarrier(transport);
        var writing = Task.Run(() => { transport.Output.GetSpan(1)[0] = 0; });
        await barrier.WaitUntilEnteredAsync();
        if (writing.IsCompleted) throw new Exception("Writer was not stalled at its real output entry.");
        barrier.Dispose(); // Same path as an assertion/unexpected exception leaving a test.
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
