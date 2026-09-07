namespace SharpLink.Abstractions;

/// <summary>Identifies the source of the effective client-wide request-timeout fallback.</summary>
public enum SharpLinkRequestTimeoutPolicySource : byte
{
    /// <summary>The client-wide fallback is disabled.</summary>
    Disabled = 0,
    /// <summary>The client uses the recommended timeout selected by the builder.</summary>
    Recommended = 1,
    /// <summary>The client uses an explicitly configured timeout.</summary>
    Custom = 2
}

/// <summary>Describes one atomically published request-timeout policy generation.</summary>
/// <param name="Generation">The monotonically increasing runtime policy generation.</param>
/// <param name="Source">The source of the effective fallback.</param>
/// <param name="Timeout">The effective fallback timeout, or <see langword="null"/> when disabled.</param>
public readonly record struct SharpLinkRequestTimeoutPolicySnapshot(
    ulong Generation,
    SharpLinkRequestTimeoutPolicySource Source,
    TimeSpan? Timeout)
{
    /// <summary>Gets whether the client-wide fallback is enabled.</summary>
    public bool Enabled => Timeout.HasValue;
}
