namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // This duration is accepted by Task.Delay on every supported runtime. Longer public retry
    // and admission delays are awaited in cancellable slices rather than rejected by the timer.
    private static readonly TimeSpan MaximumRetryOrAdmissionDelay = TimeSpan.FromMilliseconds(int.MaxValue);

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
            GetMonotonicDeadlineTimestamp(deadline, now),
            options.Metadata is { Count: > 0 } ? options.Metadata : null,
            options.WaitForReady);
    }

    private async ValueTask<ClientConnection> GetReadyConnectionAsync(
        bool waitForReady,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken,
        RpcMethodDescriptor? method = null,
        AttemptOutcomeState? attemptOutcome = null)
    {
        while (true)
        {
            attemptOutcome?.BeginAdmissionSelection();
            try
            {
                if (!_shutdownCts.IsCancellationRequested && ReadyConnectionCount != 0)
                    return method is { } descriptor
                        ? GetReadyConnection(descriptor, retrySelection: null, attemptOutcome)
                        : GetReadyConnection();

                if (!waitForReady)
                    return method is { } descriptor
                        ? GetReadyConnection(descriptor, retrySelection: null, attemptOutcome)
                        : GetReadyConnection();
            }
            catch (SharpLinkException exception) when (
                waitForReady && exception.Code == SharpLinkErrorCode.Unavailable)
            {
                if (attemptOutcome?.ShouldHonorAdmissionRetryAfter == true)
                {
                    if (attemptOutcome.RetryAfter is not { } retryAfter)
                        throw;
                    var delay = retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(1);
                    if (deadline is { } retryDeadline && WouldReachDeadline(retryDeadline, delay))
                        throw CreateDeadlineExceededException();
                    await DelayForRetryOrAdmissionAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A grant after the rejection supersedes that earlier delay, but a stale grant must
                // not suppress a retry-after returned by a later rejected endpoint.
                if (attemptOutcome?.HasAdmissionRejection == true && !attemptOutcome.HasAdmissionGrant)
                    throw;
            }

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

    private async ValueTask DelayForRetryOrAdmissionAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client has stopped.");

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        try
        {
            while (delay > MaximumRetryOrAdmissionDelay)
            {
                await Task.Delay(MaximumRetryOrAdmissionDelay, linkedCancellation.Token).ConfigureAwait(false);
                delay -= MaximumRetryOrAdmissionDelay;
            }
            await Task.Delay(delay, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _shutdownCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateConnectionClosedException("Client has stopped.");
        }
    }

    private static void AddDeadlineCandidate(
        ref DateTimeOffset? deadline,
        DateTimeOffset? candidate)
    {
        if (candidate is { } value && (deadline is null || value < deadline.Value))
            deadline = value;
    }

    private static bool WouldReachDeadline(DateTimeOffset deadline, TimeSpan delay)
        => delay >= deadline - DateTimeOffset.UtcNow;

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

    private static long GetMonotonicDeadlineTimestamp(
        DateTimeOffset? deadline,
        DateTimeOffset utcNow)
    {
        if (deadline is not { } absoluteDeadline)
            return 0;
        var remaining = absoluteDeadline - utcNow;
        if (remaining <= TimeSpan.Zero)
            return Stopwatch.GetTimestamp();
        var stopwatchTicks = remaining.TotalSeconds * Stopwatch.Frequency;
        if (stopwatchTicks >= long.MaxValue - Stopwatch.GetTimestamp())
            return long.MaxValue;
        return Stopwatch.GetTimestamp() + Math.Max(1L, (long)Math.Ceiling(stopwatchTicks));
    }

    private readonly record struct ResolvedCallControl(
        DateTimeOffset? Deadline,
        long DeadlineTimestamp,
        SharpLinkMetadata? Metadata,
        bool WaitForReady);
}
