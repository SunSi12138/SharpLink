namespace SharpLink.Abstractions;

internal static class SharpLinkTimer
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    internal static async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        while (delay > MaximumDelay)
        {
            await Task.Delay(MaximumDelay, cancellationToken).ConfigureAwait(false);
            delay -= MaximumDelay;
        }
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<bool> WaitAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            var slice = timeout > MaximumDelay ? MaximumDelay : timeout;
            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(slice);
            try
            {
                await task.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (
                waitCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
