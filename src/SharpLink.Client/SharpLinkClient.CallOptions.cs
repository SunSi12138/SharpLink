namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ResolvedCallControl ResolveCallControl(
        SharpLinkMetadata? metadata,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout)
    {
        if (methodTimeout is { } configuredMethodTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredMethodTimeout, TimeSpan.Zero);

        // Method policy overrides the client-wide fallback. These are policy-selection layers,
        // not independent lifetime caps. A parameterless [Timeout] deliberately falls back to
        // the client-wide value even on call shapes that do not otherwise use the client default.
        TimeSpan? selectedTimeout = hasMethodTimeout
            ? methodTimeout ?? (_hasRequestTimeout ? _requestTimeoutValue : null)
            : includeClientDefault && _hasRequestTimeout
                ? _requestTimeoutValue
                : null;

        var timeProvider = _runtimeContext.TimeProvider;
        var deadlineAnchor = timeProvider.GetTimestamp();
        var ambientCall = SharpLinkCallContext.Current;
        if (ambientCall is not null &&
            ambientCall.LocalRpcDeadline.HasValue &&
            ambientCall.DeadlineTimeProvider is { } inheritedTimeProvider)
        {
            var inheritedRemaining = ambientCall.LocalRpcDeadline.GetRemaining(inheritedTimeProvider);
            if (inheritedRemaining <= TimeSpan.Zero)
                throw CreateDeadlineExceededException();
            if (selectedTimeout is null || inheritedRemaining < selectedTimeout.Value)
                selectedTimeout = inheritedRemaining;
        }

        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, deadlineAnchor, timeProvider.TimestampFrequency)
            : default;
        if (deadline.IsExpired(timeProvider))
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            metadata is { Count: > 0 } ? metadata : null,
            deadline.HasValue ? new ClientLogicalCallState(deadline, timeProvider) : null);
    }

    private async ValueTask DelayForRetryOrAdmissionAsync(
        TimeSpan delay,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (_shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client has stopped.");

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        try
        {
            if (!await SharpLinkTimer.DelayAsync(
                    delay,
                    deadline,
                    _runtimeContext.TimeProvider,
                    linkedCancellation.Token).ConfigureAwait(false))
            {
                throw CreateDeadlineExceededException();
            }
        }
        catch (OperationCanceledException) when (
            _shutdownCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateConnectionClosedException("Client has stopped.");
        }
    }

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Request deadline exceeded.");

    internal sealed class ClientLogicalCallState
    {
        private readonly RpcDeadline _deadline;
        private readonly TimeProvider _timeProvider;
        private int _deadlineClaimed;

        internal ClientLogicalCallState(
            RpcDeadline deadline,
            TimeProvider timeProvider)
        {
            _deadline = deadline;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        internal bool TryEnterProgress()
        {
            if (Volatile.Read(ref _deadlineClaimed) != 0)
                return false;
            if (_deadline.IsExpired(_timeProvider))
            {
                _ = TryClaimDeadline();
                return false;
            }
            return Volatile.Read(ref _deadlineClaimed) == 0;
        }

        internal bool TryClaimDeadline()
            => Interlocked.CompareExchange(ref _deadlineClaimed, 1, 0) == 0;
    }

    internal readonly record struct ResolvedCallControl(
        RpcDeadline Deadline,
        SharpLinkMetadata? Metadata,
        ClientLogicalCallState? LogicalCall);
}
