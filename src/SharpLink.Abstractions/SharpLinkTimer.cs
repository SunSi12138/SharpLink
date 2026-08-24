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
        {
            await task.ConfigureAwait(false);
            return true;
        }

        var completionClaim = ClaimTaskCompletion(task, deadline, timeProvider);
        while (true)
        {
            if (completionClaim.IsCompleted)
                return await completionClaim.ConfigureAwait(false);
            if (deadline.IsExpired(timeProvider))
                return false;

            var timeout = deadline.GetRemaining(timeProvider);
            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
            try
            {
                return await completionClaim
                    .WaitAsync(slice, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (completionClaim.IsCompleted)
                    return await completionClaim.ConfigureAwait(false);
                if (deadline.IsExpired(timeProvider))
                    return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (completionClaim.IsCompleted)
                    return await completionClaim.ConfigureAwait(false);
                if (deadline.IsExpired(timeProvider))
                    return false;
                throw;
            }
        }
    }

    private static Task<bool> ClaimTaskCompletion(
        Task task,
        RpcDeadline deadline,
        TimeProvider timeProvider)
        => task.ContinueWith(
            static (completed, state) =>
            {
                var claim = (TaskDeadlineClaimState)state!;
                if (claim.Deadline.IsExpired(claim.TimeProvider))
                    return false;
                completed.GetAwaiter().GetResult();
                return true;
            },
            new TaskDeadlineClaimState(deadline, timeProvider),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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
        var deadline = SharpLinkTime.AddDuration(
            timeProvider.GetTimestamp(),
            timeout,
            timeProvider.TimestampFrequency);
        while (true)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = SharpLinkTime.GetRemaining(
                deadline,
                timeProvider.GetTimestamp(),
                timeProvider.TimestampFrequency);
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
                if (SharpLinkTime.GetRemaining(
                        deadline,
                        timeProvider.GetTimestamp(),
                        timeProvider.TimestampFrequency) == TimeSpan.Zero)
                {
                    return false;
                }
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
        var deadline = SharpLinkTime.AddDuration(
            timeProvider.GetTimestamp(),
            timeout,
            timeProvider.TimestampFrequency);
        return await WaitAsync(
            semaphore,
            RpcDeadline.FromTimestamp(deadline),
            timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class TaskDeadlineClaimState(
        RpcDeadline deadline,
        TimeProvider timeProvider)
    {
        public RpcDeadline Deadline { get; } = deadline;
        public TimeProvider TimeProvider { get; } = timeProvider;
    }
}
