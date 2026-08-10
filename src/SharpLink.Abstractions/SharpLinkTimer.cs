namespace SharpLink.Abstractions;

internal static class SharpLinkTimer
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);

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
            {
                await task.ConfigureAwait(false);
                return true;
            }

            var timeout = deadline.GetRemaining(timeProvider);
            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
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
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        while (true)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
            try
            {
                await task.WaitAsync(slice, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                if (task.IsCompleted)
                {
                    await task.ConfigureAwait(false);
                    return true;
                }
                if (timeout <= MaximumDelay)
                    return false;
                timeout -= MaximumDelay;
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
        }
    }

    internal static async ValueTask<bool> WaitAsync(
        SemaphoreSlim semaphore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        while (timeout > MaximumDelay)
        {
            if (await semaphore.WaitAsync(MaximumDelay, cancellationToken).ConfigureAwait(false))
                return true;
            timeout -= MaximumDelay;
        }
        return await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}
