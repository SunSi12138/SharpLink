namespace SharpLink.Abstractions;

/// <summary>Identifies the strategy in one atomically published client retry-policy generation.</summary>
public enum SharpLinkRetryPolicyKind : byte
{
    /// <summary>Retries are disabled.</summary>
    Disabled = 0,
    /// <summary>The built-in SharpLink retry decision policy is active.</summary>
    BuiltIn = 1,
    /// <summary>An application-provided <see cref="ISharpLinkRetryPolicy"/> is active.</summary>
    Custom = 2
}

/// <summary>Describes the currently published retry-policy generation.</summary>
/// <param name="Generation">The monotonically increasing runtime policy generation.</param>
/// <param name="Kind">The retry strategy.</param>
/// <param name="MaxAttempts">The bounded attempt count, or zero when disabled.</param>
/// <param name="InitialBackoff">The captured initial backoff.</param>
/// <param name="MaxBackoff">The captured maximum backoff.</param>
/// <param name="JitterRatio">The captured built-in jitter ratio.</param>
public readonly record struct SharpLinkRetryPolicySnapshot(
    ulong Generation,
    SharpLinkRetryPolicyKind Kind,
    int MaxAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff,
    double JitterRatio)
{
    /// <summary>Gets whether retries are enabled.</summary>
    public bool Enabled => Kind != SharpLinkRetryPolicyKind.Disabled;
}
