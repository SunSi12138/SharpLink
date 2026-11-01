namespace SharpLink.Abstractions;

internal static class SharpLinkTimer
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);
    private static readonly Task Never = Task.Delay(Timeout.InfiniteTimeSpan);



    internal static async ValueTask DelayAsync(
        TimeSpan delay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (delay > MaximumDelay)
        {
            await Task.Delay(MaximumDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            delay -= MaximumDelay;
        }
        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<bool> DelayAsync(
        TimeSpan delay,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (!deadline.HasValue)
        {
            await DelayAsync(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (deadline.IsExpired(timeProvider))
            return false;

        while (delay > TimeSpan.Zero)
        {
            var slice = delay > MaximumDelay ? MaximumDelay : delay;
            if (deadline.WouldExpireBeforeOrAt(slice, timeProvider))
            {
                // A delay that reaches the boundary cannot win a tie with the call deadline.
                // Wait only for the deadline/caller-cancellation contender rather than arming
                // a same-time delay whose callback ordering would otherwise decide the result.
                return await WaitAsync(
                    Never, deadline, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            using var delayCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(slice, timeProvider, delayCancellation.Token);
            try
            {
                if (!await WaitAsync(
                        delayTask, deadline, timeProvider, cancellationToken).ConfigureAwait(false))
                {
                    delayCancellation.Cancel();
                    try { await delayTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    return false;
                }
            }
            catch
            {
                delayCancellation.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                throw;
            }
            delay -= slice;
        }
        return true;
    }

    internal static async ValueTask<bool> WaitAsync(
        Task task,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (deadline.IsExpired(timeProvider))
            return false;
        if (task.IsCompleted)
            return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);

        // Preserve a caller cancellation that was already terminal before timer ownership begins.
        // Keep this after the source-completed fast path so an already-completed source retains its
        // existing priority, but before CreateTimer can advance the provider to the deadline.
        cancellationToken.ThrowIfCancellationRequested();

        // Establish timer ownership before sampling the relative delay that will represent the
        // absolute deadline. A TimeProvider is allowed to advance while CreateTimer runs; creating
        // the timer disarmed first prevents that arm latency from being added to a stale remaining
        // duration. AbsoluteDeadlineSignal then re-samples the deadline and programs the owned timer.
        using var deadlineSignal = new AbsoluteDeadlineSignal(deadline, timeProvider);
        deadlineSignal.ArmFromAbsoluteDeadline();
        if (deadlineSignal.IsCompleted)
            return false;

        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = task.WaitAsync(waitCancellation.Token);
        var completed = await Task.WhenAny(waitTask, deadlineSignal.Completion).ConfigureAwait(false);
        if (ReferenceEquals(completed, deadlineSignal.Completion))
        {
            if (task.IsCompleted)
                return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);

            waitCancellation.Cancel();
            try { await waitTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            if (task.IsCompleted)
                return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
            return false;
        }

        try
        {
            await waitTask.ConfigureAwait(false);
            return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested && !task.IsCompleted)
        {
            if (deadline.IsExpired(timeProvider))
                return false;

            // WaitAsync observes the linked waiter token. Re-publish caller cancellation with
            // the original token so the public cancellation identity contract remains intact.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            if (task.IsCompleted)
                return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
            throw;
        }
    }

    // Source-result ownership is deliberately separate from wait arbitration. A successful
    // return means only that the source is terminal and every owned wait cleanup has finished;
    // the caller must then claim and await the ORIGINAL source exactly once for its outcome.
    // Legacy WaitAsync above continues to consume/forward the source outcome unchanged.
    internal static async ValueTask<bool> WaitForCompletionAsync(
        Task task, RpcDeadline deadline, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        // Preserve the absolute deadline's priority, even over an already-faulted source.
        if (deadline.IsExpired(timeProvider)) return false;
        if (task.IsCompleted) return true;
        cancellationToken.ThrowIfCancellationRequested();

        AbsoluteDeadlineSignal? signal = null;
        CancellationTokenSource? waiterCancellation = null;
        Exception? failure = null;
        var sourceReady = false;
        try
        {
            signal = new AbsoluteDeadlineSignal(deadline, timeProvider, captureCallbackFailures: true);
            signal.ArmFromAbsoluteDeadline();
            if (!signal.IsCompleted)
            {
                waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waiter = task.WaitAsync(waiterCancellation.Token);
                var completed = await Task.WhenAny(waiter, signal.Completion).ConfigureAwait(false);
                if (ReferenceEquals(completed, signal.Completion) && !task.IsCompleted)
                    waiterCancellation.Cancel();
                // Observe the waiter's forwarding container, never consume the source outcome.
                await waiter.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (signal.CallbackFailure is null)
                {
                    if (task.IsCompleted)
                        sourceReady = !deadline.IsExpired(timeProvider);
                    else if (!deadline.IsExpired(timeProvider))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("A completion waiter ended without source completion, deadline or caller cancellation.");
                    }
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { waiterCancellation?.Dispose(); }
            catch (Exception cleanup) { failure = CombineFailures(failure, cleanup); }
            if (signal is not null)
            {
                try { await signal.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure = CombineFailures(failure, cleanup); }
                if (signal.CallbackFailure is { } callbackFailure)
                    failure = CombineFailures(failure, callbackFailure);
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        // Physical timer teardown can itself outlive the original deadline. Ownership has
        // not transferred yet: revalidate that SAME deadline only after all cleanup finished.
        // A provider failure here also leaves the source with the caller's physical owner.
        return sourceReady && !deadline.IsExpired(timeProvider);
    }

    // This watchdog owns only the deadline signal, never the source operation. The caller must
    // retain and independently join that operation after initiating exact-Session close on failure.
    // Call outside call/group gates: providers, onUnsuccessful and asynchronous disposal can run code.
    internal static async ValueTask<bool> WaitForCleanupAsync(
        Task task,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        Action onUnsuccessful)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onUnsuccessful);

        AbsoluteDeadlineSignal? signal = null;
        Exception? failure = null;
        var succeeded = false;
        Exception? observedCallbackFailure = null;
        try
        {
            if (!deadline.HasValue)
                throw new ArgumentException("Cleanup requires a finite deadline.", nameof(deadline));

            if (!deadline.IsExpired(timeProvider))
            {
                if (!task.IsCompleted)
                {
                    // Publish ownership before Change: a provider may throw while arming an
                    // already-created timer. That failure must still pass through DisposeAsync.
                    signal = new AbsoluteDeadlineSignal(deadline, timeProvider, captureCallbackFailures: true);
                    signal.ArmFromAbsoluteDeadline();
                    if (!signal.IsCompleted)
                        await Task.WhenAny(task, signal.Completion).ConfigureAwait(false);
                    if (signal.CallbackFailure is { } callbackFailure)
                    {
                        observedCallbackFailure = callbackFailure;
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(callbackFailure);
                    }
                }

                if (task.IsCompleted)
                    succeeded = await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Close promptly before joining a callback that may still be in flight. This notification
        // runs on the coordinator, NOT inside the timer callback. It must start, not await, drain.
        if (!succeeded)
        {
            try { onUnsuccessful(); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
        }

        if (signal is not null)
        {
            try { await signal.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            // A different callback can signal expiry before an overlapping callback faults.
            // The wake-up task cannot represent both outcomes. Its independent first-failure
            // slot remains authoritative after DisposeAsync joins every in-flight callback.
            if (signal.CallbackFailure is { } callbackFailure &&
                !ReferenceEquals(observedCallbackFailure, callbackFailure))
                failure = CombineFailures(failure, callbackFailure);
        }

        if (succeeded && failure is not null)
        {
            try { onUnsuccessful(); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return succeeded;
    }

    private static Exception CombineFailures(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private sealed class AbsoluteDeadlineSignal : IDisposable, IAsyncDisposable
    {
        private readonly RpcDeadline _deadline;
        private readonly TimeProvider _timeProvider;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer _timer;
        private readonly bool _captureCallbackFailures;
        private Exception? _callbackFailure;

        internal AbsoluteDeadlineSignal(
            RpcDeadline deadline, TimeProvider timeProvider, bool captureCallbackFailures = false)
        {
            _deadline = deadline;
            _timeProvider = timeProvider;
            _captureCallbackFailures = captureCallbackFailures;

            // Create the timer without a due time so timer ownership is established before the
            // absolute deadline is projected into a relative delay.
            _timer = timeProvider.CreateTimer(
                static state => ((AbsoluteDeadlineSignal)state!).OnTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        internal Task Completion => _completion.Task;

        internal bool IsCompleted => _completion.Task.IsCompleted;

        internal Exception? CallbackFailure => Volatile.Read(ref _callbackFailure);

        private void OnTimer()
        {
            try
            {
                if (_deadline.IsExpired(_timeProvider))
                {
                    _completion.TrySetResult();
                    return;
                }

                ArmFromAbsoluteDeadline();
            }
            catch (Exception exception) when (_captureCallbackFailures)
            {
                // Capture independently: another callback may already have signaled expiry.
                // Retain only the first failure; the completion source is merely a wake-up.
                Interlocked.CompareExchange(ref _callbackFailure, exception, null);
                _completion.TrySetResult();
            }
        }

        internal void ArmFromAbsoluteDeadline()
        {
            var remaining = _deadline.GetRemaining(_timeProvider);
            if (remaining == TimeSpan.Zero)
            {
                _completion.TrySetResult();
                return;
            }

            var dueTime = remaining > MaximumDelay ? MaximumDelay : remaining;
            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);

            // The clock can cross the absolute deadline after GetRemaining but before Change
            // installs the relative delay. Complete the signal now instead of waiting for that
            // stale delay; TrySetResult also tolerates a timer callback winning the same race.
            if (_deadline.IsExpired(_timeProvider))
                _completion.TrySetResult();
        }

        public void Dispose() => _timer.Dispose();

        public ValueTask DisposeAsync() => _timer.DisposeAsync();
    }

    private static async ValueTask<bool> ClaimTaskCompletionAsync(
        Task task,
        RpcDeadline deadline,
        TimeProvider timeProvider)
    {
        // Task.WaitAsync forwards source success, faults, and cancellation directly. Re-arbitrate
        // every source terminal outcome at one boundary before observing/rethrowing it so a source
        // task that becomes terminal after the RPC deadline cannot replace DeadlineExceeded.
        if (deadline.IsExpired(timeProvider))
            return false;
        await task.ConfigureAwait(false);
        return true;
    }



    internal static async ValueTask<bool> WaitAsync(
        Task task,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        // Generic graceful-drain/remove waits are not RPC lifetimes and therefore do not inherit
        // the modular half-ring restriction used by RpcDeadline. Preserve TimeSpan.MaxValue as an
        // effectively unbounded wait, but keep it provider-driven and bounded to the runtime's
        // timer range so fake/custom providers retain deterministic timer ownership.
        if (timeout == TimeSpan.MaxValue)
        {
            while (true)
            {
                if (task.IsCompleted)
                {
                    await task.ConfigureAwait(false);
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await task.WaitAsync(
                        MaximumDelay,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException) when (!task.IsCompleted)
                {
                }
            }
        }

        var deadline = RpcDeadline.Create(timeout, timeProvider);
        while (true)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline.GetRemaining(timeProvider);
            if (remaining == TimeSpan.Zero)
                return false;
            var slice = remaining > MaximumDelay ? MaximumDelay : remaining;
            try
            {
                await task.WaitAsync(slice, timeProvider, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                if (task.IsCompleted)
                {
                    await task.ConfigureAwait(false);
                    return true;
                }
                if (deadline.IsExpired(timeProvider))
                    return false;
            }
        }
    }

    internal static async ValueTask<bool> WaitAsync(
        SemaphoreSlim semaphore,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (true)
        {
            if (deadline.IsExpired(timeProvider))
                return false;

            var timeout = deadline.GetRemaining(timeProvider);
            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waitTask = semaphore.WaitAsync(waitCancellation.Token);
            try
            {
                await waitTask.WaitAsync(slice, timeProvider, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                waitCancellation.Cancel();
                try
                {
                    await waitTask.ConfigureAwait(false);
                    if (!deadline.IsExpired(timeProvider))
                        return true;

                    // The timeout won, but the semaphore was released before the
                    // cancellation reached its waiter. Return that permit so an
                    // expired capacity wait cannot steal a later caller's slot.
                    semaphore.Release();
                    return false;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                if (deadline.IsExpired(timeProvider))
                    return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!deadline.IsExpired(timeProvider))
                    throw;

                waitCancellation.Cancel();
                try
                {
                    await waitTask.ConfigureAwait(false);
                    semaphore.Release();
                }
                catch (OperationCanceledException)
                {
                }
                return false;
            }
        }
    }



    internal static async ValueTask<bool> WaitAsync(
        SemaphoreSlim semaphore,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (semaphore.Wait(0))
            return true;
        if (timeout == TimeSpan.Zero)
            return false;
        return await WaitAsync(
            semaphore,
            RpcDeadline.Create(timeout, timeProvider),
            timeProvider,
            cancellationToken).ConfigureAwait(false);
    }
}
