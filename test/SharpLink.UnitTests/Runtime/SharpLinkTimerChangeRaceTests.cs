namespace SharpLink.UnitTests.Runtime;

public class SharpLinkTimerChangeRaceTests
{
    [Test]
    [Arguments(false, 5)]
    [Arguments(true, 5)]
    [Arguments(false, 6)]
    [Arguments(true, 6)]
    public async Task DeadlineCrossedDuringFiniteChangeShouldCompleteWithoutAnotherTick(bool delay, int advanceSeconds)
    {
        var provider = new AdvancingOnChangeTimeProvider(TimeSpan.FromSeconds(advanceSeconds));
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        using var cleanup = new CancellationTokenSource();
        var wait = delay
            ? SharpLinkTimer.DelayAsync(TimeSpan.MaxValue, deadline, provider, cleanup.Token).AsTask()
            : SharpLinkTimer.WaitAsync(source.Task, deadline, provider, cleanup.Token).AsTask();

        try
        {
            Ensure(provider.FiniteChangeCount == 1, "the test must cross the finite Change window");
            Ensure(wait.IsCompleted,
                "an expiry crossed during finite Change must complete without a second clock advance");
            Ensure(!await wait, "the absolute deadline must win over the stale relative timer");
            Ensure(provider.ActiveTimerCount == 0, "the stale timer must be disposed");
            Ensure(provider.DisposeCount == 1, "the waiter must dispose its timer exactly once");
            Ensure(!source.Task.IsCompleted, "the deadline must not complete the source task");
        }
        finally
        {
            cleanup.Cancel();
            await ObserveAsync(wait);
        }
    }

    [Test]
    public async Task DeadlineCrossedDuringChunkRearmShouldCompleteWithoutAnotherTick()
    {
        var provider = new AdvancingOnChangeTimeProvider(TimeSpan.FromSeconds(5), changeNumber: 2);
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(SharpLinkTimer.MaximumDelay + TimeSpan.FromSeconds(5), provider);
        using var cleanup = new CancellationTokenSource();
        var wait = SharpLinkTimer.WaitAsync(source.Task, deadline, provider, cleanup.Token).AsTask();

        try
        {
            Ensure(!wait.IsCompleted, "the first timer slice must leave the deadline pending");
            provider.FireAfterAdvancingWithoutTimers(SharpLinkTimer.MaximumDelay);
            Ensure(provider.FiniteChangeCount == 2, "the callback must arm the final slice");
            Ensure(!await wait.WaitAsync(TimeSpan.FromSeconds(2)),
                "an expiry crossed during callback rearm must not need another clock advance");
            Ensure(provider.ActiveTimerCount == 0, "the callback-rearmed timer must be disposed");
            Ensure(provider.DisposeCount == 1, "the callback must not take over timer disposal");
        }
        finally
        {
            cleanup.Cancel();
            await ObserveAsync(wait);
        }
    }

    [Test]
    public async Task CallbackAndPostArmExpiryShouldShareOneCompletionAndOneTimerOwner()
    {
        var provider = new AdvancingOnChangeTimeProvider(TimeSpan.FromSeconds(5), fireDuringChange: true);
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);

        var expired = await SharpLinkTimer.WaitAsync(source.Task, deadline, provider);

        Ensure(!expired, "the callback and post-arm check must agree on expiry");
        Ensure(provider.CallbackCount == 1, "the test must invoke the callback during Change");
        Ensure(provider.ActiveTimerCount == 0, "the timer must be disposed after both completion attempts");
        Ensure(provider.DisposeCount == 1, "only the waiter owns disposal");
    }

    [Test]
    public async Task CallerCancellationBeforeFiniteChangeShouldRetainItsTokenAndAvoidTimerOwnership()
    {
        var provider = new AdvancingOnChangeTimeProvider(TimeSpan.FromSeconds(5));
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await ObserveAsync(
            SharpLinkTimer.WaitAsync(source.Task, deadline, provider, cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token,
            "pre-existing caller cancellation must retain its original token");
        Ensure(provider.FiniteChangeCount == 0 && provider.ActiveTimerCount == 0,
            "pre-existing cancellation must not create a timer");
    }

    [Test]
    public async Task SourceCompletionBeforeDeadlineShouldDisposeNormally()
    {
        var provider = new AdvancingOnChangeTimeProvider(TimeSpan.Zero);
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(source.Task, deadline, provider).AsTask();

        source.SetResult();

        Ensure(await wait.WaitAsync(TimeSpan.FromSeconds(2)), "a source completed before expiry must still win");
        Ensure(provider.ActiveTimerCount == 0 && provider.DisposeCount == 1,
            "normal source completion must dispose the owned timer exactly once");
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AdvancingOnChangeTimeProvider(
        TimeSpan advance,
        int changeNumber = 1,
        bool fireDuringChange = false) : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private TimerCallback? _callback;
        private object? _state;
        private int _finiteChangeCount;
        private int _disposeCount;
        private int _callbackCount;

        internal int ActiveTimerCount => _inner.ActiveTimerCount;
        internal int FiniteChangeCount => Volatile.Read(ref _finiteChangeCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int CallbackCount => Volatile.Read(ref _callbackCount);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new AdvancingTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
        }

        internal void FireAfterAdvancingWithoutTimers(TimeSpan elapsed)
        {
            _inner.AdvanceWithoutRunningTimers(elapsed);
            FireCallback();
        }

        private void FireCallback()
        {
            Interlocked.Increment(ref _callbackCount);
            (_callback ?? throw new InvalidOperationException("timer not created"))(_state);
        }

        private bool ChangeTimer(ITimer timer, TimeSpan dueTime, TimeSpan period)
        {
            var selectedChange = dueTime != Timeout.InfiniteTimeSpan &&
                Interlocked.Increment(ref _finiteChangeCount) == changeNumber;
            if (selectedChange)
                _inner.AdvanceWithoutRunningTimers(advance);
            var changed = timer.Change(dueTime, period);
            if (selectedChange && fireDuringChange)
                FireCallback();
            return changed;
        }

        private sealed class AdvancingTimer(AdvancingOnChangeTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.ChangeTimer(inner, dueTime, period);

            public void Dispose()
            {
                Interlocked.Increment(ref owner._disposeCount);
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
