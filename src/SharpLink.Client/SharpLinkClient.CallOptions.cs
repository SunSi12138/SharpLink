namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ResolvedCallControl ResolveCallControl(
        SharpLinkMetadata? metadata,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout)
    {
        var lifetimeSource = ClientCallLifetimeSource.None;
        return ResolveCallControl(
            metadata,
            includeClientDefault,
            hasMethodTimeout,
            methodTimeout,
            ref lifetimeSource);
    }

    private ResolvedCallControl ResolveCallControl(
        SharpLinkMetadata? metadata,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout,
        ref ClientCallLifetimeSource lifetimeSource,
        bool allocateSharedLogicalCall = true)
    {
        if (methodTimeout is { } configuredMethodTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredMethodTimeout, TimeSpan.Zero);

        // Capture exactly one client fallback generation at the logical-call creation boundary.
        // Later runtime updates cannot alter this call's selected timeout or frozen deadline.
        var requestTimeoutPolicy = CaptureRequestTimeoutGeneration().Policy;
        // Telemetry detail follows the same next-logical-call boundary. This keeps the logical
        // activity, optional enrichments, delayed stream enumeration, and retry attempts on one
        // immutable detail generation even if the control plane publishes a replacement mid-call.
        var telemetryDetailMode = CaptureTelemetryDetailGeneration().Mode;

        // Method policy overrides the client-wide fallback. These are policy-selection layers,
        // not independent lifetime caps. A parameterless [Timeout] deliberately falls back to
        // the client-wide value even on call shapes that do not otherwise use the client default.
        TimeSpan? selectedTimeout;
        if (hasMethodTimeout && methodTimeout is { } explicitMethodTimeout)
        {
            selectedTimeout = explicitMethodTimeout;
            lifetimeSource = ClientCallLifetimeSource.MethodTimeout;
        }
        else if ((hasMethodTimeout || includeClientDefault) && requestTimeoutPolicy.HasTimeout)
        {
            selectedTimeout = requestTimeoutPolicy.Timeout;
            lifetimeSource = requestTimeoutPolicy.Source.ToLifetimeSource();
        }
        else
        {
            selectedTimeout = null;
            lifetimeSource = ClientCallLifetimeSource.None;
        }

        var timeProvider = _runtimeContext.TimeProvider;
        // Untimed calls have no local anchor to preserve. Inherited deadlines below
        // still observe their own shared-clock or cross-clock projection boundary.
        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, timeProvider.GetTimestamp(), timeProvider.TimestampFrequency)
            : default;

        var ambientCall = SharpLinkCallContext.Current;
        if (ambientCall is not null &&
            ambientCall.LocalRpcDeadline.HasValue &&
            ambientCall.DeadlineTimeProvider is { } inheritedTimeProvider)
        {
            RpcDeadline inheritedDeadline;
            long comparisonTimestamp;
            if (ReferenceEquals(inheritedTimeProvider, timeProvider))
            {
                // One shared monotonic clock already gives us the exact parent boundary. Preserve
                // it directly: converting the parent to a remaining duration and re-anchoring that
                // duration can either double-charge a scheduling gap or extend the parent's hard
                // cap, depending on which side of the two clock reads the gap lands on.
                comparisonTimestamp = timeProvider.GetTimestamp();
                inheritedDeadline = ambientCall.LocalRpcDeadline;
                if (inheritedDeadline.IsExpired(comparisonTimestamp))
                {
                    lifetimeSource = ClientCallLifetimeSource.InheritedTimeBudget;
                    throw CreateDeadlineExceededException();
                }
            }
            else
            {
                // Across genuinely different providers no absolute monotonic timestamp is
                // transferable. Project the observed parent remaining duration onto the child
                // clock, but charge child-clock time consumed while obtaining that observation so
                // the projection can be conservative and can never extend the observed lifetime.
                var projectionStarted = timeProvider.GetTimestamp();
                var inheritedRemaining = ambientCall.LocalRpcDeadline.GetRemaining(inheritedTimeProvider);
                comparisonTimestamp = timeProvider.GetTimestamp();
                if (inheritedRemaining <= TimeSpan.Zero)
                {
                    lifetimeSource = ClientCallLifetimeSource.InheritedTimeBudget;
                    throw CreateDeadlineExceededException();
                }

                var projectionElapsed = SharpLinkTime.GetElapsed(
                    projectionStarted,
                    comparisonTimestamp,
                    timeProvider.TimestampFrequency);
                if (projectionElapsed >= inheritedRemaining)
                {
                    lifetimeSource = ClientCallLifetimeSource.InheritedTimeBudget;
                    throw CreateDeadlineExceededException();
                }
                inheritedRemaining -= projectionElapsed;
                inheritedDeadline = RpcDeadline.Create(
                    inheritedRemaining,
                    comparisonTimestamp,
                    timeProvider.TimestampFrequency);
            }

            if (!deadline.HasValue || inheritedDeadline.IsEarlierOrEqual(deadline, comparisonTimestamp))
            {
                deadline = inheritedDeadline;
                lifetimeSource = ClientCallLifetimeSource.InheritedTimeBudget;
            }
        }

        if (deadline.IsExpired(timeProvider))
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            metadata is { Count: > 0 } ? metadata : null,
            deadline.HasValue && allocateSharedLogicalCall
                ? new ClientLogicalCallState()
                : null,
            lifetimeSource,
            telemetryDetailMode,
            timeProvider);
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

    // The only state that genuinely has to be shared between the participants of one logical call
    // is the deadline-claim arbitration. Every immutable input (frozen deadline, time provider,
    // captured retry generation, telemetry detail) lives on the resolved call control, so this
    // object stays a single mutable flag and cannot drift out of sync with a copied control.
    internal sealed class ClientLogicalCallState
    {
        private int _deadlineClaimed;

        internal bool TryEnterProgress(RpcDeadline deadline, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            if (!deadline.HasValue)
                return true;
            if (Volatile.Read(ref _deadlineClaimed) != 0)
                return false;
            if (deadline.IsExpired(timeProvider))
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
        ClientLogicalCallState? LogicalCall,
        ClientCallLifetimeSource LifetimeSource = ClientCallLifetimeSource.None,
        SharpLinkTelemetryDetailMode TelemetryDetailMode = SharpLinkTelemetryDetailMode.Detailed,
        TimeProvider? TimeProvider = null,
        ClientRetryGeneration? RetryGeneration = null);
}
