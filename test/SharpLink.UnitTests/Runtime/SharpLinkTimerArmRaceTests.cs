namespace SharpLink.UnitTests.Runtime;

public class SharpLinkTimerArmRaceTests
{
    [Test]
    public async Task TaskWaitShouldRecheckDeadlineAfterArmingRelativeTimeout()
    {
        var provider = new AdvancingOnFirstTimerTimeProvider(TimeSpan.FromSeconds(5));
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);

        var wait = SharpLinkTimer.WaitAsync(
            owner.Task,
            deadline,
            provider,
            CancellationToken.None).AsTask();

        Ensure(!await wait.WaitAsync(TimeSpan.FromSeconds(2)),
            "an absolute deadline that expires while its relative timeout is armed must win immediately");
        Ensure(provider.ActiveTimerCount == 0,
            "the stale relative timeout must be canceled after post-arm deadline arbitration");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AdvancingOnFirstTimerTimeProvider(TimeSpan advance) : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private int _advanced;

        public int ActiveTimerCount => _inner.ActiveTimerCount;

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (Interlocked.Exchange(ref _advanced, 1) == 0)
                _inner.AdvanceWithoutRunningTimers(advance);
            return _inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
