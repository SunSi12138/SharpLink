namespace SharpLink.Abstractions;

internal static class SharpLinkTimer
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);
    private static readonly Task Never = Task.Delay(Timeout.InfiniteTimeSpan);

    internal static ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => DelayAsync(delay, TimeProvider.System, cancellationToken);

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

    private sealed class AbsoluteDeadlineSignal : IDisposable
    {
        private readonly RpcDeadline _deadline;
        private readonly TimeProvider _timeProvider;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer _timer;

        internal AbsoluteDeadlineSignal(RpcDeadline deadline, TimeProvider timeProvider)
        {
            _deadline = deadline;
            _timeProvider = timeProvider;

            // Create the timer without a due time so timer ownership is established before the
            // absolute deadline is projected into a relative delay.
            _timer = timeProvider.CreateTimer(
                static state => ((AbsoluteDeadlineSignal)state!).OnTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            ArmFromAbsoluteDeadline();
        }

        internal Task Completion => _completion.Task;

        internal bool IsCompleted => _completion.Task.IsCompleted;

        private void OnTimer()
        {
            if (_deadline.IsExpired(_timeProvider))
            {
                _completion.TrySetResult();
                return;
            }

            ArmFromAbsoluteDeadline();
        }

        private void ArmFromAbsoluteDeadline()
        {
            var remaining = _deadline.GetRemaining(_timeProvider);
            if (remaining == TimeSpan.Zero)
            {
                _completion.TrySetResult();
                return;
            }

            var dueTime = remaining > MaximumDelay ? MaximumDelay : remaining;
            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => _timer.Dispose();
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
        CancellationToken cancellationToken = default)
        => await WaitAsync(task, timeout, TimeProvider.System, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
        => await WaitAsync(
            semaphore,
            timeout,
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);

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
