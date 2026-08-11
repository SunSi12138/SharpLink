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
        var timeProvider = _runtimeContext.TimeProvider;
        var utcNow = timeProvider.GetUtcNow();
        var timestampNow = timeProvider.GetTimestamp();
        DateTimeOffset? utcDeadline = null;
        AddDeadlineCandidate(ref utcDeadline, options.Deadline);
        if (options.Timeout is { } timeout)
            AddDeadlineCandidate(ref utcDeadline, AddTimeout(utcNow, timeout));
        if (methodTimeout is { } explicitMethodTimeout)
            AddDeadlineCandidate(ref utcDeadline, AddTimeout(utcNow, explicitMethodTimeout));
        if ((includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)
            AddDeadlineCandidate(ref utcDeadline, AddTimeout(utcNow, _requestTimeoutValue));

        var deadline = utcDeadline is { } value
            ? RpcDeadline.Create(
                value,
                utcNow,
                timestampNow,
                timeProvider.TimestampFrequency)
            : default;
        if (deadline.IsExpired(timestampNow))
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            options.Metadata is { Count: > 0 } ? options.Metadata : null,
            options.WaitForReady);
    }

    private async ValueTask<ClientConnection> GetReadyConnectionAsync(
        bool waitForReady,
        RpcDeadline deadline,
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
                    if (WouldReachDeadline(deadline, delay))
                        throw CreateDeadlineExceededException();
                    await DelayForRetryOrAdmissionAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A grant after the rejection supersedes that earlier delay, but a stale grant must
                // not suppress a retry-after returned by a later rejected endpoint.
                if (attemptOutcome?.HasAdmissionRejection == true && !attemptOutcome.HasAdmissionGrant)
                    throw;
            }

            if (Volatile.Read(ref _stopStarted) != 0 ||
                State == SharpLinkConnectionState.Stopped ||
                _shutdownCts.IsCancellationRequested)
                throw CreateConnectionClosedException("Client has stopped.");

            var signal = Volatile.Read(ref _readySignal).Task;
            if (!deadline.HasValue)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await SharpLinkTimer.WaitAsync(
                    signal,
                    deadline,
                    _runtimeContext.TimeProvider,
                    cancellationToken).ConfigureAwait(false))
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
                await SharpLinkTimer.DelayAsync(
                    MaximumRetryOrAdmissionDelay,
                    _runtimeContext.TimeProvider,
                    linkedCancellation.Token).ConfigureAwait(false);
                delay -= MaximumRetryOrAdmissionDelay;
            }
            await SharpLinkTimer.DelayAsync(
                delay,
                _runtimeContext.TimeProvider,
                linkedCancellation.Token).ConfigureAwait(false);
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

    private bool WouldReachDeadline(RpcDeadline deadline, TimeSpan delay)
        => deadline.WouldExpireBeforeOrAt(delay, _runtimeContext.TimeProvider);

    private static DateTimeOffset AddTimeout(DateTimeOffset now, TimeSpan timeout)
    {
        var maximum = DateTimeOffset.MaxValue - now;
        return timeout >= maximum ? DateTimeOffset.MaxValue : now.Add(timeout);
    }

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Request deadline exceeded.");

    private readonly record struct ResolvedCallControl(
        RpcDeadline Deadline,
        SharpLinkMetadata? Metadata,
        bool WaitForReady);
}
