using System.Buffers.Binary;
using SharpLink.FlowStatePhaseB;

namespace SharpLink.UnitTests.Runtime;

public class PhaseBWireBoundaryTests
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    [Test]
    public async Task RealReceiverConnectionThresholdReturnsBothExactStreamIdentities()
    {
        await using var fixture = new PhaseBWireBoundaryFixture(64);
        fixture.Sender.Writer.Release();
        var a = await fixture.OpenAsync(7, 0);
        var b = await fixture.OpenAsync(7, ushort.MaxValue);
        var first = await fixture.Sender.EnqueueAsync(a, 16);
        await first.Completion;
        await fixture.ReceiveAsync(first);
        Require((await fixture.DrainCreditsAsync()).Count == 0, "one stream is below both receiver thresholds");
        var second = await fixture.Sender.EnqueueAsync(b, 16);
        await second.Completion;
        await fixture.ReceiveAsync(second);
        var updates = await fixture.DrainCreditsAsync();
        Require(updates.Count == 2 && updates.Exists(x => x.RequestId == 7 && x.Update.StreamId == 0) &&
            updates.Exists(x => x.RequestId == 7 && x.Update.StreamId == ushort.MaxValue), "connection threshold flushes every contributor");
        Require(updates.TrueForAll(x => x.Update.Credit == 16 && x.Observation.Returned == 16 && x.Observation.Excess == 0), "actual receiver byte credits reconcile");
        Require((await fixture.Sender.Credits.SnapshotAsync()).Outstanding == 0, "no credit stranded on other stream");
    }

    [Test]
    public async Task ReceiverTerminalFlushReturnsBelowThresholdCreditAfterSenderClose()
    {
        await using var fixture = new PhaseBWireBoundaryFixture(64);
        fixture.Sender.Writer.Release();
        var lease = await fixture.OpenAsync(11, 3);
        var ticket = await fixture.Sender.EnqueueAsync(lease, 16);
        await ticket.Completion;
        await fixture.ReceiveAsync(ticket);
        Require((await fixture.DrainCreditsAsync()).Count == 0, "receiver retains below-threshold bytes");
        await fixture.Sender.Credits.CloseAsync(lease);
        Require((await fixture.Sender.Credits.SnapshotAsync()).Retained == 1, "uncredited close leaves tombstone");
        fixture.Receiver.StreamManager.CompleteStream(11, 3, exception: null);
        var updates = await fixture.DrainCreditsAsync();
        Require(updates.Count == 1 && updates[0].Update.Credit == 16, "actual completion callback flushes bytes");
        Require((await fixture.Sender.Credits.SnapshotAsync()) is { Free: 64, Outstanding: 0, Retained: 0 }, "wire return retires tombstone");
    }

    [Test]
    public async Task RealEarlyWireCreditDoesNotReleasePausedWriterPin()
    {
        await using var fixture = new PhaseBWireBoundaryFixture(32);
        var lease = await fixture.OpenAsync(13, 0);
        var ticket = await fixture.Sender.EnqueueAsync(lease, 16);
        await fixture.Sender.Writer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ReceiveAsync(ticket);
        await fixture.Sender.Credits.CloseAsync(lease);
        var returned = await fixture.DrainCreditsAsync();
        Require(returned.Count == 1 && returned[0].Observation.Returned == 16, "real credit arrives before writer completion");
        Require((await fixture.Sender.Credits.SnapshotAsync()) is { Free: 32, Outstanding: 0, Publications: 1, Retained: 1 }, "pin survives wire credit and close");
        fixture.Sender.Writer.Release();
        await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var next = await fixture.Sender.Credits.OpenAsync(13, 0);
        Require(ReferenceEquals(next.State, lease.State) && next.Generation != lease.Generation, "reuse only after settlement");
    }

    [Test]
    public async Task RealOversizedPeerReturnResumesAnotherCreditStarvedWriter()
    {
        await using var fixture = new PhaseBWireBoundaryFixture(16);
        fixture.Sender.Writer.Release();
        var a = await fixture.OpenAsync(1, 0);
        var b = await fixture.OpenAsync(1, 1);
        var first = await fixture.Sender.EnqueueAsync(a, 32);
        await first.Completion;
        var waiting = fixture.Sender.EnqueueAsync(b, 1);
        Require((await fixture.Sender.Credits.SnapshotAsync()) is { Free: -16, Waiters: 1 }, "second frame cannot enter pump before repayment");
        await fixture.ReceiveAsync(first);
        var updates = await fixture.DrainCreditsAsync();
        Require(updates.Count == 1 && updates[0].Update.Credit == 32, "receiver repays actual oversized payload");
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Completion;
        await fixture.ReceiveAsync(second);
        fixture.Receiver.StreamManager.CompleteStream(1, 1, exception: null);
        await fixture.DrainCreditsAsync();
        await fixture.Sender.Credits.CloseAsync(a);
        await fixture.Sender.Credits.CloseAsync(b);
        Require((await fixture.Sender.Credits.SnapshotAsync()) is { Free: 16, Outstanding: 0, Waiters: 0, Publications: 0 }, "wire-only progress conserves all credit");
    }

    [Test]
    public async Task EightStreamsPerformRepeatedRealReceiverCreditCycles()
    {
        await using var fixture = new PhaseBWireBoundaryFixture(32);
        fixture.Sender.Writer.Release();
        var streams = new GrantAuthority.Lease[8];
        for (ushort i = 0; i < streams.Length; i++) streams[i] = await fixture.OpenAsync(23, i);
        for (var cycle = 0; cycle < 64; cycle++)
            foreach (var lease in streams)
            {
                var ticket = await fixture.Sender.EnqueueAsync(lease, 16);
                await ticket.Completion;
                await fixture.ReceiveAsync(ticket);
                var updates = await fixture.DrainCreditsAsync();
                Require(updates.Count == 1 && updates[0].Observation is { Returned: 16, Excess: 0 }, "every return originates at negotiated receiver");
            }
        foreach (var lease in streams) await fixture.Sender.Credits.CloseAsync(lease);
        Require((await fixture.Sender.Credits.SnapshotAsync()) is { Free: 32, Unspent: 0, Outstanding: 0, Publications: 0, Retained: 0 }, "512 actual StreamData/WindowUpdate round trips settle");
    }

    [Test]
    public async Task SegmentedCoalescedWireFramesPreserveSignedRequestBitsAndStreamIdentity()
    {
        await using var owner = new GrantAuthority(16, 32, 0);
        var key = long.MinValue + 7;
        var a = await owner.OpenAsync(key, ushort.MaxValue);
        owner.Commit(await owner.AcquireAsync(a, 16));
        var unknown = PhaseBWireBoundaryFixture.CreditFrame(2, ushort.MaxValue, 9);
        var actual = PhaseBWireBoundaryFixture.CreditFrame(key, ushort.MaxValue, 16);
        var bytes = new byte[unknown.Length + actual.Length];
        unknown.CopyTo(bytes, 0); actual.CopyTo(bytes, unknown.Length);
        var head = new Segment(bytes.AsMemory(0, 3));
        var tail = head.Append(bytes.AsMemory(3, 13)).Append(bytes.AsMemory(16));
        var frames = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        var result = await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, frames);
        Require(result.Count == 2 && !result[0].Observation.Matched && result[1].RequestId == key && result[1].Observation.Returned == 16, "real parser decodes split header and coalesced frames");
    }

    [Test]
    public async Task InvalidAndTruncatedCreditFramesNeverReachAuthority()
    {
        await using var owner = new GrantAuthority(16, 16, 16);
        var frame = PhaseBWireBoundaryFixture.CreditFrame(1, 1, 16);
        var submissions = owner.QueueSubmissions;
        foreach (var credit in new uint[] { 0, uint.MaxValue })
        {
            var bad = (byte[])frame.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(bad.Length - sizeof(uint)), credit);
            try { await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(bad)); throw new Exception("invalid wire accepted"); }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ProtocolViolation) { }
        }
        try { await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(frame.AsMemory(0, frame.Length - 1))); throw new Exception("truncated wire accepted"); }
        catch (InvalidDataException) { }
        Require(owner.QueueSubmissions == submissions, "framing/payload validation precedes mutation");
    }

    [Test]
    public async Task BalancedWireTraceMatchesFrozenControllerCreditAtEveryStep()
    {
        var legacy = new StreamFlowController(64, 128, 1024);
        await using var owner = new GrantAuthority(64, 128, 64);
        var a = await owner.OpenAsync(31, 0);
        var b = await owner.OpenAsync(31, 1);
        for (var cycle = 0; cycle < 128; cycle++)
        {
            foreach (var lease in new[] { a, b })
            {
                await legacy.AcquireSendCreditAsync(31, lease.State.StreamId, 16, CancellationToken.None);
                owner.Commit(await owner.AcquireAsync(lease, 16));
                var snapshot = await owner.SnapshotAsync();
                Require(snapshot.Free + snapshot.Unspent == legacy.SendConnectionCredit, "admission balance matches including unspent grants");
            }
            foreach (var credit in new[] { 3, 5, 8 })
                foreach (var lease in new[] { a, b })
                {
                    var wire = PhaseBWireBoundaryFixture.CreditFrame(31, lease.State.StreamId, credit);
                    var result = await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(wire));
                    legacy.ApplyWindowUpdate(31, lease.State.StreamId, credit);
                    var snapshot = await owner.SnapshotAsync();
                    Require(result[0].Observation.Excess == 0 && snapshot.Free + snapshot.Unspent == legacy.SendConnectionCredit, "ordered partial wire return matches at every step");
                }
        }
    }

    [Test]
    public async Task DuplicateCreditExposesIndependentConnectionClampCompatibilityGap()
    {
        var legacy = new StreamFlowController(16, 32, 1024);
        await using var owner = new GrantAuthority(16, 32, 0);
        foreach (ushort stream in new ushort[] { 0, 1 })
        {
            var lease = await owner.OpenAsync(41, stream);
            owner.Commit(await owner.AcquireAsync(lease, 16));
            await legacy.AcquireSendCreditAsync(41, stream, 16, CancellationToken.None);
        }
        var sameFrame = PhaseBWireBoundaryFixture.CreditFrame(41, 0, 16);
        await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(sameFrame));
        legacy.ApplyWindowUpdate(41, 0, 16);
        Require(legacy.SendConnectionCredit == 16 && (await owner.SnapshotAsync()).Free == 16, "first return agrees");
        var duplicate = await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(sameFrame));
        legacy.ApplyWindowUpdate(41, 0, 16);
        var snapshot = await owner.SnapshotAsync();
        Require(duplicate[0].Observation is { Returned: 0, Excess: 16 } && snapshot.Outstanding == 16, "model exposes duplicate without crediting the other stream");
        Require(legacy.SendConnectionCredit == 32 && snapshot.Free == 16, "CHARACTERIZATION: legacy independent clamp and model conservation differ; not compatibility acceptance");
    }

    [Test]
    public async Task KeyOnlyLateFrameAfterIdentityReuseHasNoWireGenerationDiscriminator()
    {
        var legacy = new StreamFlowController(16, 16, 1024);
        await using var owner = new GrantAuthority(16, 16, 0, maxStreams: 1);
        var old = await owner.OpenAsync(51, 0);
        var wire = PhaseBWireBoundaryFixture.CreditFrame(51, 0, 8);
        owner.Commit(await owner.AcquireAsync(old, 8));
        await legacy.AcquireSendCreditAsync(51, 0, 8, CancellationToken.None);
        await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(wire));
        legacy.ApplyWindowUpdate(51, 0, 8);
        await owner.CloseAsync(old); legacy.CompleteSendStream(51, 0);
        var next = await owner.OpenAsync(51, 0);
        owner.Commit(await owner.AcquireAsync(next, 8));
        await legacy.AcquireSendCreditAsync(51, 0, 8, CancellationToken.None);
        await PhaseBWireBoundaryFixture.ReplayCreditsAsync(owner, new ReadOnlySequence<byte>(wire));
        legacy.ApplyWindowUpdate(51, 0, 8);
        Require(legacy.SendConnectionCredit == 16 && (await owner.SnapshotAsync()).Free == 16, "both consume identical key-only bits against current identity; neither knows the old wire generation");
        try { await owner.AcquireAsync(old, 1); throw new Exception("old internal lease accepted"); }
        catch (InvalidOperationException) { }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
