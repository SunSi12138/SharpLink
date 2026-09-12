namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ReconnectPolicyGeneration? _reconnectPolicyConfiguration;

    public SharpLinkReconnectPolicy GetReconnectPolicy()
        => CaptureReconnectPolicy().Policy;

    public void UpdateReconnectPolicy(SharpLinkReconnectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ReconnectPolicyGeneration? previous;
        lock (_stateGate)
        {
            EnsureReconnectPolicyPublicationAllowed();
            var current = CaptureReconnectPolicy();
            if (current.Policy == policy)
                return;
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("The reconnect policy generation is exhausted.");

            var candidate = new ReconnectPolicyGeneration(current.Generation + 1, policy);
            // Link the captured generation before publishing the new head. An in-flight reconnect
            // attempt may therefore reconcile its completion against the replacement policy even
            // when several updates race with the same attempt.
            current.SetSuccessor(candidate);
            Volatile.Write(ref _reconnectPolicyConfiguration, candidate);
            previous = current;
        }

        // Policy publication never rewrites topology-owned failure streaks, backoff positions, or
        // stable-ready timestamps. It only wakes waits captured from the previous generation so the
        // existing reconnect owner can reconcile that live state against the newly published bounds.
        previous.SignalChanged();
    }

    private ReconnectPolicyGeneration CaptureReconnectPolicy()
        => Volatile.Read(ref _reconnectPolicyConfiguration)
           ?? throw new InvalidOperationException("Reconnect policy was not initialized by the client build plan.");

    private bool IsCurrentReconnectPolicyGeneration(ReconnectPolicyGeneration generation)
    {
        var current = Volatile.Read(ref _reconnectPolicyConfiguration);
        return current is not null && generation.Generation <= current.Generation;
    }

    private async ValueTask<bool> WaitForReconnectDelayAsync(
        TimeSpan baseDelay,
        ReconnectPolicyGeneration generation,
        CancellationToken cancellationToken)
    {
        // Capture exactly one complete policy for this armed wait. A later publication cancels only
        // this delay; when the owner loops it captures the replacement generation as one unit.
        var policy = generation.Policy;
        var delay = _reconnectJitter.Apply(
            baseDelay,
            policy.JitterMinimumFactor,
            policy.JitterMaximumFactor);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generation.ChangedToken);
        try
        {
            await SharpLinkTimer.DelayAsync(
                delay,
                _runtimeContext.TimeProvider,
                linkedCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (
            generation.ChangedToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static TimeSpan ResolveReconnectDelay(long storedTicks, SharpLinkReconnectPolicy policy)
    {
        if (storedTicks <= 0)
            return policy.InitialDelay;
        return TimeSpan.FromTicks(Math.Clamp(storedTicks, policy.InitialDelay.Ticks, policy.MaxBackoff.Ticks));
    }

    internal static TimeSpan NextReconnectDelay(TimeSpan current, SharpLinkReconnectPolicy policy)
    {
        var scaled = current.Ticks * policy.BackoffMultiplier;
        if (!double.IsFinite(scaled) || scaled >= policy.MaxBackoff.Ticks)
            return policy.MaxBackoff;
        var ticks = Math.Max(current.Ticks, (long)Math.Ceiling(scaled));
        return TimeSpan.FromTicks(Math.Min(ticks, policy.MaxBackoff.Ticks));
    }

    internal bool HasReachedReconnectStableWindow(
        long readyTimestamp,
        bool hasReadyTimestamp,
        SharpLinkReconnectPolicy policy)
    {
        if (policy.StableResetWindow == TimeSpan.Zero)
            return true;
        if (!hasReadyTimestamp)
            return false;
        return _runtimeContext.TimeProvider.GetElapsedTime(readyTimestamp) >= policy.StableResetWindow;
    }

    internal static TimeSpan ResolveReconnectCompletionDelay(
        TimeSpan baseDelay,
        bool reconnected,
        SharpLinkReconnectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!reconnected)
            return NextReconnectDelay(baseDelay, policy);
        return policy.StableResetWindow == TimeSpan.Zero
            ? policy.InitialDelay
            : baseDelay;
    }

    private void EnsureReconnectPolicyPublicationAllowed()
    {
        var state = State;
        if (Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
        {
            throw new InvalidOperationException(
                $"Client state '{state}' does not accept reconnect policy updates.");
        }
    }

    private sealed class ReconnectPolicyGeneration
    {
        private readonly CancellationTokenSource _changed = new();
        private readonly SharpLinkReconnectPolicy _publishedPolicy;
        private ReconnectPolicyGeneration? _successor;

        internal ReconnectPolicyGeneration(ulong generation, SharpLinkReconnectPolicy policy)
        {
            Generation = generation;
            _publishedPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        internal ulong Generation { get; }

        // A reconnect attempt is not cancelled by policy publication. If it completes after one or
        // more updates, follow the successor chain so its state transition uses the newest complete
        // policy; an armed wait captures this property before awaiting and is then cancelled normally.
        internal SharpLinkReconnectPolicy Policy
        {
            get
            {
                var current = this;
                while (Volatile.Read(ref current._successor) is { } successor)
                    current = successor;
                return current._publishedPolicy;
            }
        }

        internal CancellationToken ChangedToken => _changed.Token;

        internal void SetSuccessor(ReconnectPolicyGeneration successor)
        {
            ArgumentNullException.ThrowIfNull(successor);
            Volatile.Write(ref _successor, successor);
        }

        internal void SignalChanged()
        {
            try
            {
                _changed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
