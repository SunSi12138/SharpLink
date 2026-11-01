namespace SharpLink.UnitTests.Runtime;

public sealed class WakeupSignalCleanupTests
{
    [Test]
    public async Task ThrowingTimerDisposalCannotStrandClaimedWakeOrNextGeneration()
    {
        var signal = new WakeupSignal();
        var clock = new ThrowingTimerProvider();
        var wait = signal.WaitAsync(clock, TimeSpan.FromSeconds(1));
        Exception? failure = null;
        try { signal.Signal(); }
        catch (Exception exception) { failure = exception; }
        if (!ReferenceEquals(failure, clock.Failure))
            throw new InvalidOperationException("the exact timer cleanup failure must remain observable");
        if (!await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("the claimed producer wake must still complete");
        var next = signal.WaitAsync();
        if (next.IsCompleted)
            throw new InvalidOperationException("failed cleanup must not manufacture a later generation wake");
        signal.Signal();
        if (!await next.AsTask().WaitAsync(TimeSpan.FromSeconds(2)) || clock.DisposeCount != 1)
            throw new InvalidOperationException("each claimed timer must be detached and disposed exactly once");
    }

    private sealed class ThrowingTimerProvider : TimeProvider
    {
        internal readonly Exception Failure = new InvalidOperationException("injected timer cleanup failure");
        internal int DisposeCount;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new ThrowingTimer(this);
        private sealed class ThrowingTimer(ThrowingTimerProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { owner.DisposeCount++; throw owner.Failure; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
