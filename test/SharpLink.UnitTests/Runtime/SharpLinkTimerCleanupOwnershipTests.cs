namespace SharpLink.UnitTests.Runtime;

public class SharpLinkTimerCleanupOwnershipTests
{
    [Test]
    public async Task CompletedCleanupShouldNotCreateTimerOrNotifyFailure()
    {
        var provider = new ManualTimeProvider();
        var notifications = 0;

        Ensure(await SharpLinkTimer.WaitForCleanupAsync(
            Task.CompletedTask, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => notifications++), "completed cleanup must use the no-timer path");
        Ensure(provider.ActiveTimerCount == 0 && notifications == 0,
            "successful synchronous cleanup must not acquire a timer or request close");
    }

    [Test]
    public async Task CleanupExpiryShouldDisposeTimerWithoutCompletingSource()
    {
        var provider = new ManualTimeProvider();
        var source = NewSignal();
        var notifications = 0;
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            source.Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => Interlocked.Increment(ref notifications)).AsTask();

        Ensure(provider.ActiveTimerCount == 1, "one whole-branch watchdog owns exactly one timer");
        provider.Advance(TimeSpan.FromSeconds(5));

        Ensure(!await wait, "absolute cleanup deadline must expire");
        Ensure(provider.ActiveTimerCount == 0 && notifications == 1,
            "expiry must request close once and tear down its timer");
        Ensure(!source.Task.IsCompleted, "expiry is not evidence of source completion");
        source.SetResult();
        await source.Task;
    }

    [Test]
    public async Task ArmFailureShouldDisposeAlreadyOwnedTimerAndPreserveFailure()
    {
        var provider = new TrackingTimeProvider { ThrowOnChangeNumber = 1 };
        var notifications = 0;
        var failure = await CaptureFailureAsync(SharpLinkTimer.WaitForCleanupAsync(
            NewSignal().Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => notifications++).AsTask());

        Ensure(ReferenceEquals(failure, provider.ChangeFailure), "arming failure must remain observable");
        Ensure(provider.ActiveTimerCount == 0 && provider.AsyncDisposeCount == 1,
            "arming failure must asynchronously dispose its already-created timer exactly once");
        Ensure(notifications == 1, "arming failure must request exact-Session close");
    }

    [Test]
    public async Task ExistingWaitArmFailureShouldAlsoDisposeAlreadyOwnedTimer()
    {
        var provider = new TrackingTimeProvider { ThrowOnChangeNumber = 1 };
        var failure = await CaptureFailureAsync(SharpLinkTimer.WaitAsync(
            NewSignal().Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider).AsTask());

        Ensure(ReferenceEquals(failure, provider.ChangeFailure), "existing wait must preserve arm failure");
        Ensure(provider.ActiveTimerCount == 0 && provider.SyncDisposeCount == 1,
            "existing wait must dispose ownership acquired before its failed Change");
    }

    [Test]
    public async Task ExpiryMustRequestCloseBeforeJoiningHeldTimerCallback()
    {
        var provider = new TrackingTimeProvider { HoldCallback = true };
        var source = NewSignal();
        var closeRequested = NewSignal();
        var notifications = 0;
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            source.Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => { Interlocked.Increment(ref notifications); closeRequested.TrySetResult(); }).AsTask();
        var callback = provider.FireAsync(TimeSpan.FromSeconds(5));

        try
        {
            await provider.CallbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await provider.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!wait.IsCompleted, "watchdog completion must join the still-running timer callback");
            Ensure(!source.Task.IsCompleted, "timer teardown must not complete the owned publisher");
        }
        finally
        {
            provider.ReleaseCallback.Set();
            await callback;
        }

        Ensure(!await wait, "expiry must remain the result after callback quiescence");
        Ensure(provider.AsyncDisposeCount == 1 && provider.SyncDisposeCount == 0 &&
            provider.ActiveTimerCount == 0 && notifications == 1,
            "only one asynchronous timer owner may tear down the watchdog");
        source.SetResult();
        await source.Task;
    }

    [Test]
    public async Task SourceSuccessMustAlsoJoinHeldTimerCallback()
    {
        var provider = new TrackingTimeProvider { HoldCallback = true };
        var source = NewSignal();
        var notifications = 0;
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            source.Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => Interlocked.Increment(ref notifications)).AsTask();
        var callback = provider.FireAsync(TimeSpan.FromSeconds(1));

        try
        {
            await provider.CallbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            source.SetResult();
            await provider.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!wait.IsCompleted, "source success must not detach a still-running callback");
        }
        finally
        {
            provider.ReleaseCallback.Set();
            await callback;
        }

        Ensure(await wait, "source success before expiry must be preserved after quiescence");
        Ensure(notifications == 0 && provider.ActiveTimerCount == 0 && provider.AsyncDisposeCount == 1,
            "successful teardown must release the timer without closing a healthy Session");
    }

    [Test]
    public async Task CallbackRearmFailureMustNotifyCloseAndJoinCallback()
    {
        var provider = new TrackingTimeProvider { ThrowOnChangeNumber = 2, HoldCallback = true };
        var closeRequested = NewSignal();
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            NewSignal().Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => closeRequested.TrySetResult()).AsTask();
        var callback = provider.FireAsync(TimeSpan.FromSeconds(1));

        try
        {
            await provider.CallbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await provider.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!wait.IsCompleted, "callback failure must not skip callback quiescence");
        }
        finally
        {
            provider.ReleaseCallback.Set();
            await callback;
        }

        Ensure(ReferenceEquals(await CaptureFailureAsync(wait), provider.ChangeFailure),
            "callback rearm failure must reach the coordinator rather than escape the timer thread");
        Ensure(provider.ActiveTimerCount == 0 && provider.AsyncDisposeCount == 1,
            "failed callback must retain one timer teardown owner");
    }

    [Test]
    public async Task OverlappingCallbackFailureMustSurviveExpirySignalAndJoinEveryCallback()
    {
        var provider = new TrackingTimeProvider
        {
            ThrowOnChangeNumber = 2,
            HoldFailingChange = true,
            HoldCallback = true
        };
        var closeRequested = NewSignal();
        var notifications = 0;
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            NewSignal().Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => { Interlocked.Increment(ref notifications); closeRequested.TrySetResult(); }).AsTask();
        // The first callback samples a live deadline, then stalls immediately before rearm fails.
        var failingCallback = provider.FireAsync(TimeSpan.FromSeconds(1));
        Task? expiryCallback = null;
        try
        {
            await provider.FailingChangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // A second real invocation overlaps it and completes the signal successfully at expiry.
            expiryCallback = provider.FireAsync(TimeSpan.FromSeconds(4));
            await provider.CallbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await provider.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(provider.ActiveCallbackCount == 2,
                "the regression requires two simultaneously active timer callbacks");

            provider.ReleaseCallback.Set();
            await expiryCallback.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(provider.ActiveCallbackCount == 1 && !wait.IsCompleted,
                "one callback exiting must not satisfy teardown while the other is still active");
        }
        finally
        {
            provider.ReleaseCallback.Set();
            provider.ReleaseFailingChange.Set();
            await failingCallback;
            if (expiryCallback is not null)
                await expiryCallback;
        }

        Ensure(ReferenceEquals(await CaptureFailureAsync(wait), provider.ChangeFailure),
            "a callback fault after successful expiry signaling must be retained exactly once");
        Ensure(provider.ActiveCallbackCount == 0 && provider.ActiveTimerCount == 0 &&
            provider.AsyncDisposeCount == 1 && notifications == 1,
            "all callbacks and timer ownership must join without a duplicate close notification");
    }

    [Test]
    public async Task NotificationAndDisposalFailuresMustNotSkipTimerCleanupOrHideSourceFailure()
    {
        var provider = new TrackingTimeProvider { ThrowOnDispose = true };
        var source = NewSignal();
        var sourceFailure = new InvalidOperationException("source failed");
        var notificationFailure = new InvalidOperationException("close notification failed");
        var notifications = 0;
        var wait = SharpLinkTimer.WaitForCleanupAsync(
            source.Task, RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider,
            () => { notifications++; throw notificationFailure; }).AsTask();
        source.SetException(sourceFailure);

        var failure = await CaptureFailureAsync(wait);
        Ensure(failure is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Contains(sourceFailure) &&
            aggregate.Flatten().InnerExceptions.Contains(notificationFailure) &&
            aggregate.Flatten().InnerExceptions.Contains(provider.DisposeFailure),
            "source, notification and teardown failures must all remain observable");
        Ensure(provider.ActiveTimerCount == 0 && provider.AsyncDisposeCount == 1 && notifications == 1,
            "throwing close notification must not skip or duplicate timer teardown");
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    // Manual monotonic clock plus a real callback lifetime barrier. Unlike the basic manual timer,
    // this timer distinguishes disarming from joining a callback already captured by its scheduler.
    private sealed class TrackingTimeProvider : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private TrackingTimer? _timer;
        private int _changeCount;
        private int _asyncDisposeCount;
        private int _syncDisposeCount;
        internal int ThrowOnChangeNumber { get; init; }
        internal bool ThrowOnDispose { get; init; }
        internal bool HoldCallback { get; init; }
        internal bool HoldFailingChange { get; init; }
        internal Exception ChangeFailure { get; } = new InvalidOperationException("Change failed");
        internal Exception DisposeFailure { get; } = new InvalidOperationException("DisposeAsync failed");
        internal TaskCompletionSource CallbackReturned { get; } = NewSignal();
        internal TaskCompletionSource FailingChangeEntered { get; } = NewSignal();
        internal TaskCompletionSource DisposeStarted { get; } = NewSignal();
        internal ManualResetEventSlim ReleaseCallback { get; } = new(false);
        internal ManualResetEventSlim ReleaseFailingChange { get; } = new(false);
        internal int ActiveCallbackCount => _timer?.ActiveCallbackCount ?? 0;
        internal int ActiveTimerCount => _inner.ActiveTimerCount;
        internal int AsyncDisposeCount => Volatile.Read(ref _asyncDisposeCount);
        internal int SyncDisposeCount => Volatile.Read(ref _syncDisposeCount);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TrackingTimer(this, callback, state);
            timer.Inner = _inner.CreateTimer(_ => timer.Invoke(), null, dueTime, period);
            _timer = timer;
            return timer;
        }

        internal Task FireAsync(TimeSpan elapsed)
        {
            _inner.AdvanceWithoutRunningTimers(elapsed);
            var timer = _timer ?? throw new InvalidOperationException("timer not created");
            return Task.Run(timer.Invoke);
        }

        private sealed class TrackingTimer(TrackingTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private readonly Lock _gate = new();
            private TaskCompletionSource _callbacksDrained = CompletedSignal();
            private bool _disposed;
            private int _activeCallbacks;
            internal ITimer Inner { get; set; } = null!;
            internal int ActiveCallbackCount
            {
                get { lock (_gate) return _activeCallbacks; }
            }

            private static TaskCompletionSource CompletedSignal()
            {
                var signal = NewSignal();
                signal.SetResult();
                return signal;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Interlocked.Increment(ref owner._changeCount) == owner.ThrowOnChangeNumber)
                {
                    if (owner.HoldFailingChange)
                    {
                        owner.FailingChangeEntered.TrySetResult();
                        owner.ReleaseFailingChange.Wait();
                    }
                    throw owner.ChangeFailure;
                }
                return Inner.Change(dueTime, period);
            }

            internal void Invoke()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    if (_activeCallbacks++ == 0)
                        _callbacksDrained = NewSignal();
                }
                try
                {
                    callback(state);
                    owner.CallbackReturned.TrySetResult();
                    if (owner.HoldCallback)
                        owner.ReleaseCallback.Wait();
                }
                finally
                {
                    lock (_gate)
                    {
                        if (--_activeCallbacks == 0)
                            _callbacksDrained.TrySetResult();
                    }
                }
            }

            public void Dispose()
            {
                Interlocked.Increment(ref owner._syncDisposeCount);
                lock (_gate)
                    _disposed = true;
                Inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._asyncDisposeCount);
                Task callbacksDrained;
                lock (_gate)
                {
                    _disposed = true;
                    callbacksDrained = _callbacksDrained.Task;
                }
                Inner.Dispose();
                owner.DisposeStarted.TrySetResult();
                await callbacksDrained.ConfigureAwait(false);
                if (owner.ThrowOnDispose)
                    throw owner.DisposeFailure;
            }
        }
    }
}
