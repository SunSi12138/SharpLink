namespace SharpLink.Abstractions;

/// <summary>
/// Immutable client reconnect timing policy. The policy governs reconnect waits only; the first
/// connection attempt and logical-RPC retry backoff use their own independent lifecycles.
/// </summary>
public sealed record SharpLinkReconnectPolicy
{
    /// <summary>Creates one validated reconnect policy.</summary>
    public SharpLinkReconnectPolicy(
        TimeSpan initialDelay,
        TimeSpan maxBackoff,
        double backoffMultiplier,
        double jitterMinimumFactor,
        double jitterMaximumFactor,
        TimeSpan stableResetWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        if (maxBackoff < initialDelay)
            throw new ArgumentOutOfRangeException(nameof(maxBackoff), "MaxBackoff must be greater than or equal to InitialDelay.");
        if (!double.IsFinite(backoffMultiplier) || backoffMultiplier < 1d)
            throw new ArgumentOutOfRangeException(nameof(backoffMultiplier), "BackoffMultiplier must be finite and at least 1.");
        if (!double.IsFinite(jitterMinimumFactor) || jitterMinimumFactor <= 0d)
            throw new ArgumentOutOfRangeException(nameof(jitterMinimumFactor), "JitterMinimumFactor must be finite and positive.");
        if (!double.IsFinite(jitterMaximumFactor) || jitterMaximumFactor < jitterMinimumFactor)
            throw new ArgumentOutOfRangeException(nameof(jitterMaximumFactor), "JitterMaximumFactor must be finite and greater than or equal to JitterMinimumFactor.");
        ArgumentOutOfRangeException.ThrowIfLessThan(stableResetWindow, TimeSpan.Zero);

        InitialDelay = initialDelay;
        MaxBackoff = maxBackoff;
        BackoffMultiplier = backoffMultiplier;
        JitterMinimumFactor = jitterMinimumFactor;
        JitterMaximumFactor = jitterMaximumFactor;
        StableResetWindow = stableResetWindow;
    }

    /// <summary>Gets the base delay used by a fresh reconnect sequence.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>Gets the maximum unjittered reconnect backoff.</summary>
    public TimeSpan MaxBackoff { get; }

    /// <summary>Gets the multiplier applied after a failed reconnect attempt.</summary>
    public double BackoffMultiplier { get; }

    /// <summary>Gets the inclusive lower multiplicative jitter bound.</summary>
    public double JitterMinimumFactor { get; }

    /// <summary>Gets the inclusive upper multiplicative jitter bound.</summary>
    public double JitterMaximumFactor { get; }

    /// <summary>
    /// Gets the continuously-ready duration required before a later disconnect resets backoff to
    /// <see cref="InitialDelay"/>. Zero resets immediately after a successful reconnect.
    /// </summary>
    public TimeSpan StableResetWindow { get; }
}
