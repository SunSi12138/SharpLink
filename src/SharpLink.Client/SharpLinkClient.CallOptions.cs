namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ResolvedCallControl ResolveCallControl(
        SharpLinkCallOptions options,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout)
    {
        if (options.Timeout is { } optionTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(optionTimeout, TimeSpan.Zero);
        if (methodTimeout is { } configuredMethodTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredMethodTimeout, TimeSpan.Zero);
        if (options.EnableCompression)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                "Request compression is not available until a compression capability is registered.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = null;
        AddDeadlineCandidate(ref deadline, options.Deadline);
        if (options.Timeout is { } timeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, timeout));
        if (methodTimeout is { } explicitMethodTimeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, explicitMethodTimeout));
        if ((includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, _requestTimeoutValue));

        if (deadline is { } expired && expired <= now)
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            options.Metadata is { Count: > 0 } ? options.Metadata : null,
            options.WaitForReady);
    }

    private async ValueTask<RpcSession> GetReadySessionAsync(
        bool waitForReady,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (State == SharpLinkConnectionState.Ready && ReadyConnectionCount != 0)
                return GetReadySession();

            if (!waitForReady)
                return GetReadySession();
            if (State == SharpLinkConnectionState.Stopped || _shutdownCts.IsCancellationRequested)
                throw CreateConnectionClosedException("Client has stopped.");

            var signal = Volatile.Read(ref _readySignal).Task;
            if (deadline is not { } absoluteDeadline)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw CreateDeadlineExceededException();
            try
            {
                await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw CreateDeadlineExceededException();
            }
        }
    }

    private static void AddDeadlineCandidate(
        ref DateTimeOffset? deadline,
        DateTimeOffset? candidate)
    {
        if (candidate is { } value && (deadline is null || value < deadline.Value))
            deadline = value;
    }

    private static DateTimeOffset AddTimeout(DateTimeOffset now, TimeSpan timeout)
    {
        try
        {
            return now.Add(timeout);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, exception.Message);
        }
    }

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Request deadline exceeded.");

    private void CompleteStreamLifetime(long requestId)
    {
        if (_streamCallLifetimes.TryRemove(requestId, out var lifetime))
            lifetime.Dispose();
    }

    private sealed class StreamCallLifetime(
        TimeoutRegistration timeoutRegistration,
        PooledCancellationRegistration cancellationRegistration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            timeoutRegistration.Dispose();
            cancellationRegistration.Dispose();
        }
    }

    private readonly record struct ResolvedCallControl(
        DateTimeOffset? Deadline,
        SharpLinkMetadata? Metadata,
        bool WaitForReady);
}
