namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    [Test]
    public async Task NestedAbortMustNotReleaseTheOuterCleanupUse()
    {
        using var fixture = new Fixture(maxStreams: 1);
        var old = fixture.Owner.Open(11, 0);
        var packet = new Packet(1);
        await fixture.Owner.EnqueueAsync(old, packet, 1);
        var drain = fixture.Owner.DrainAcceptedFramesAsync(old);
        var expected = new IOException("outer abort");
        var callbackVisited = false;
        var keyRemainedPinned = false;
        Exception? callbackError = null;
        var scheduler = new ImmediateQueueScheduler(() =>
        {
            callbackVisited = true;
            try
            {
                fixture.Owner.Abort(old, new IOException("nested abort"));
                fixture.PumpChanges();
                try { _ = fixture.Owner.Open(11, 0); }
                catch (SharpLinkException) { keyRemainedPinned = true; }
            }
            catch (Exception error) { callbackError = error; }
        });
        var observed = drain.ContinueWith(static task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.None, scheduler);
        fixture.Owner.Abort(old, expected);
        await observed.WaitAsync(Limit);
        Ensure(callbackVisited && callbackError is null && keyRemainedPinned,
            "nested cleanup prematurely released the outer lifecycle use");
        fixture.PumpChanges();
        var fresh = fixture.Owner.Open(11, 0);
        Ensure(!ReferenceEquals(old, fresh) && packet.Returns == 1,
            "outer cleanup left a permanent pin or returned its packet twice");
        await fixture.Owner.EnqueueAsync(fresh, new Packet(2), 1);
        Ensure(Marker(fixture.PeekAndFinish()) == 2, "replacement did not progress after final cleanup");
    }

    [Test]
    public async Task BudgetJoinAfterAbortMustObserveThePublishedError()
    {
        using var fixture = new Fixture(maxStreams: 2, slots: 1, prepared: 24, maxPacket: 24);
        var blocker = fixture.Owner.Open(22, 0);
        await fixture.Owner.EnqueueAsync(blocker, new Packet(1), 1);
        var victim = fixture.Owner.Open(11, 0);
        var expected = new IOException("abort before budget join");
        // Exact helper checkpoint: Enqueue already owns its producer bit and has checked
        // the slot, but has not registered a prepared-byte waiter yet. This is not claimed
        // as a full asynchronous Enqueue interleaving; it exercises the actual join helper.
        lock (victim.Gate) victim.ProducerActive = 1;
        try
        {
            fixture.Owner.Abort(victim, expected);
            fixture.PumpChanges();
            var pending = fixture.Owner.AcquirePreparedBudgetAsync(victim, 24, CancellationToken.None);
            // WaitForGrantAsync can still be resuming after its inner waiter is rejected.
            // Require completion with the exact error WITHOUT releasing the blocking stream,
            // not synchronous completion of that outer async method.
            Exception? actual = null;
            try { await pending.AsTask().WaitAsync(Limit); }
            catch (Exception error) when (error is not TimeoutException) { actual = error; }
            Ensure(ReferenceEquals(actual, expected), "late budget rejection lost the abort exception");
            Ensure(fixture.Owner.PreparedBytes == 24, "victim borrowed or returned the blocker's bytes");
        }
        finally
        {
            lock (victim.Gate) victim.ProducerActive &= ~1;
            fixture.Owner.Abort(victim, expected);
        }
        fixture.PumpChanges();
        var fresh = fixture.Owner.Open(11, 0);
        Ensure(!ReferenceEquals(victim, fresh), "cleanup failed to release the retired key");
        Ensure(Marker(fixture.PeekAndFinish()) == 1, "abort altered the independent stream");
        Ensure(fixture.Owner.PreparedBytes == 0, "prepared accounting did not settle");
        fixture.Owner.AssertCreditConservation();
    }
}
