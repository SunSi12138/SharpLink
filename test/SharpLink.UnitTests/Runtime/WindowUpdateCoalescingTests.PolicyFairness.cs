namespace SharpLink.UnitTests.Runtime;

public partial class WindowUpdateCoalescingTests
{
    [Test]
    public async Task NormalInterleaveStillCountsSixtyFourOriginalFrames()
    {
        await using var fixture = new Fixture();
        var payload = new byte[16 * 1024];
        fixture.Session.SendPacket(fixture.CreateOwner(Frame(ProtocolV2FrameType.Response, 1, payload)));
        var parked = await fixture.Writer.NextAsync();
        for (ulong requestId = 2; requestId <= 131; requestId++)
            fixture.Session.SendPacket(fixture.CreateOwner(Frame(ProtocolV2FrameType.Response, requestId, payload)));
        for (var index = 0; index < 3; index++)
            fixture.Session.SendWindowUpdate(500, 1, 4);
        parked.Succeed();

        // These above-threshold normal frames each flush, but the interleave budget must
        // remain independent of those flush boundaries. The leading parked frame is #1.
        for (ulong requestId = 2; requestId <= 64; requestId++)
        {
            var normal = await fixture.Writer.NextAsync();
            Ensure(normal.Bytes.AsSpan().SequenceEqual(Frame(ProtocolV2FrameType.Response, requestId, payload)),
                "normal frames must preserve original FIFO order up to the 64-frame interleave");
            Ensure(fixture.PendingCandidateCount == 0, "normal-only flushes must not acquire candidate metadata");
            normal.Succeed();
        }
        var progress = await fixture.Writer.NextAsync();
        Ensure(progress.Bytes.AsSpan().SequenceEqual(Update(500, 1, 12)),
            "three existing updates must be served together at the original 64-normal-frame boundary");
        progress.Succeed();
        for (ulong requestId = 65; requestId <= 131; requestId++)
        {
            var normal = await fixture.Writer.NextAsync();
            Ensure(normal.Bytes.AsSpan().SequenceEqual(Frame(ProtocolV2FrameType.Response, requestId, payload)),
                "the remaining original normal frames must follow the compact progress group in order");
            normal.Succeed();
        }
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
        Ensure(fixture.Writer.FlushCount == 132, "compaction must not alter any original flush boundary");
    }

    [Test]
    public async Task SupportedLivePolicyChangeDuringHeldFlushKeepsCreditImmediate()
    {
        await using var fixture = new Fixture(SharpLinkPerformanceProfile.LowLatency);
        fixture.Session.SendWindowUpdate(1, 1, 4);
        var first = await fixture.Writer.NextAsync();
        Ensure(first.Bytes.AsSpan().SequenceEqual(Update(1, 1, 4)), "initial LowLatency update must flush alone");
        fixture.Session.SendWindowUpdate(1, 1, 4);
        fixture.Session.SendWindowUpdate(1, 1, 4);
        Ensure(fixture.Policy.Publish(1024 * 1024, TimeSpan.FromSeconds(30)),
            "the supported live API must publish an explicit batching policy");
        first.Succeed();
        var subsequent = await fixture.Writer.NextAsync();
        Ensure(subsequent.Bytes.AsSpan().SequenceEqual(Update(1, 1, 8)),
            "the new policy may combine only the two later originals, not the already flushed update");
        Ensure(fixture.Writer.FlushCount == 2,
            "progress must flush immediately without waiting for the new 30-second normal batching window");
        subsequent.Succeed();
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
        Ensure(fixture.PendingCandidateCount == 0, "the live generation change must not leak batch metadata");
    }
}
