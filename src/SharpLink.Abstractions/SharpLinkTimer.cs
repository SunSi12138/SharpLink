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
        while (true)
        {
            if (deadline.IsExpired(timeProvider))
                return false;
            if (task.IsCompleted)
                return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);

            var timeout = deadline.GetRemaining(timeProvider);
            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
            try
            {
                await task.WaitAsync(slice, timeProvider, cancellationToken).ConfigureAwait(false);
                return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                if (deadline.IsExpired(timeProvider))
                    return false;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested && !task.IsCompleted)
            {
                if (deadline.IsExpired(timeProvider))
                    return false;
                throw;
            }
            catch
            {
                if (task.IsCompleted)
                    return await ClaimTaskCompletionAsync(task, deadline, timeProvider).ConfigureAwait(false);
                throw;
            }
        }
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
