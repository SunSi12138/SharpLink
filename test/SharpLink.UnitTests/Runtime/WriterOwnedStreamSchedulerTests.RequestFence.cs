namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    [Test]
    public async Task RequestEmissionMustPrecedeOpeningAReadyStream()
    {
        using var fixture = new Fixture();
        var emission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Owner.OpenAfterRequestEmissionAsync(11, 0, emission.Task);
        Ensure(!pending.IsCompleted && fixture.Owner.RetainedStreams == 0,
            "pending Request emission must not create a ready-stream identity");
        Ensure(!fixture.Owner.HasWork, "pending Request emission must not schedule DATA");
        emission.SetResult();
        var stream = await pending.AsTask().WaitAsync(Limit);
        await fixture.Owner.EnqueueAsync(stream, new Packet(7), 1);
        Ensure(Marker(fixture.PeekAndFinish()) == 7, "successful predecessor did not enable DATA");
    }

    [Test]
    public async Task FailedRequestEmissionMustNotAllocateAStream()
    {
        using var fixture = new Fixture();
        var expected = new IOException("Request output failure");
        var pending = fixture.Owner.OpenAfterRequestEmissionAsync(11, 0, Task.FromException(expected));
        Exception? actual = null;
        try { _ = await pending.AsTask().WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { actual = error; }
        Ensure(ReferenceEquals(actual, expected), "Request failure identity changed");
        Ensure(fixture.Owner.RetainedStreams == 0 && fixture.Owner.PreparedBytes == 0,
            "failed predecessor consumed stream or preparation capacity");
    }

    [Test]
    public async Task CanceledRequestObserverMustNotCancelDurableEmission()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var emission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Owner.OpenAfterRequestEmissionAsync(11, 0, emission.Task, cancellation.Token);
        cancellation.Cancel();
        var canceled = false;
        try { _ = await pending.AsTask().WaitAsync(Limit); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { canceled = true; }
        Ensure(canceled && !emission.Task.IsCompleted, "observer stole ownership of Request emission");
        Ensure(fixture.Owner.RetainedStreams == 0, "canceled predecessor created a stream");
        emission.SetException(new IOException("late Request output failure"));
    }

    [Test]
    public async Task PreCanceledOpenMustNotAdmitEvenWhenRequestAlreadySucceeded()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pending = fixture.Owner.OpenAfterRequestEmissionAsync(11, 0, Task.CompletedTask, cancellation.Token);
        var canceled = false;
        try { _ = await pending.AsTask().WaitAsync(Limit); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { canceled = true; }
        Ensure(canceled && fixture.Owner.RetainedStreams == 0, "completed Request bypassed call cancellation");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AbortCleanupMustPinTheKeyUntilKeyedBudgetCleanupFinishes(bool remote)
    {
        using var fixture = new Fixture(window: 4, connection: 4, maxStreams: 1,
            slots: 1, prepared: 24, maxPacket: 24);
        var old = fixture.Owner.Open(11, 0);
        var firstPacket = new Packet(1);
        await fixture.Owner.EnqueueAsync(old, firstPacket, 1);
        var drain = fixture.Owner.DrainAcceptedFramesAsync(old);
        var expected = new IOException("old lifecycle abort");
        var callbackVisited = false;
        var insideAbort = false;
        var callbackWasInsideAbort = false;
        WriterOwnedStreamScheduler.StreamHandle? prematurelyOpened = null;
        Task? replacementSend = null;
        Exception? callbackFailure = null;
        var scheduler = new ImmediateQueueScheduler(() =>
        {
            callbackVisited = true;
            callbackWasInsideAbort = insideAbort;
            try
            {
                // Standard Task continuation scheduling can reenter here while Abort is
                // notifying drain waiters, before its key-scoped preparation cleanup.
                // This fixture is the sole writer; no timing, sleeps or private test hook.
                fixture.PumpChanges();
                try { prematurelyOpened = fixture.Owner.Open(11, 0); }
                catch (SharpLinkException) { }
                if (prematurelyOpened is not null)
                    replacementSend = fixture.Owner.EnqueueAsync(prematurelyOpened, new Packet(2), 1).AsTask();
            }
            catch (Exception error) { callbackFailure = error; }
        });
        var notification = drain.ContinueWith(static task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.None, scheduler);
        insideAbort = true;
        try { fixture.Owner.Abort(old, expected, remote); }
        finally { insideAbort = false; }
        await notification.WaitAsync(Limit);
        Exception? replacementFailure = null;
        if (replacementSend is not null)
        {
            try { await replacementSend.WaitAsync(Limit); }
            catch (Exception error) when (error is not TimeoutException) { replacementFailure = error; }
        }
        Ensure(callbackVisited && callbackWasInsideAbort && callbackFailure is null,
            "the deterministic drain-notification interleaving was not exercised");
        Ensure(prematurelyOpened is null,
            "old Abort retired its key before cleanup; old failure reached replacement: " +
            ReferenceEquals(replacementFailure, expected));
        fixture.PumpChanges();
        var fresh = fixture.Owner.Open(11, 0);
        Ensure(!ReferenceEquals(old, fresh), "post-cleanup open must own a new lifecycle");
        await fixture.Owner.EnqueueAsync(fresh, new Packet(3), 1);
        Ensure(Marker(fixture.PeekAndFinish()) == 3, "old cleanup damaged the fresh stream");
        Ensure(firstPacket.Returns == 1 && fixture.Owner.PreparedBytes == 0,
            "abort preparation ownership was not released exactly once");
        fixture.Owner.AssertCreditConservation();
    }

    private sealed class ImmediateQueueScheduler(Action onQueue) : TaskScheduler
    {
        protected override IEnumerable<Task>? GetScheduledTasks() => Array.Empty<Task>();
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task)
        {
            onQueue();
            _ = TryExecuteTask(task);
        }
    }
}
