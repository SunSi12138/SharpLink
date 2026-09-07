namespace SharpLink.Abstractions;

/// <summary>
/// Provides the bounded retry-loop settings copied when a runtime retry-policy generation is published.
/// </summary>
public interface ISharpLinkRetryOptions
{
    /// <summary>Gets the maximum number of attempts, including the initial attempt.</summary>
    int MaxAttempts { get; }
    /// <summary>Gets the initial built-in retry backoff.</summary>
    TimeSpan InitialBackoff { get; }
    /// <summary>Gets the maximum built-in retry backoff.</summary>
    TimeSpan MaxBackoff { get; }
    /// <summary>Gets the proportional built-in jitter range from zero through one.</summary>
    double JitterRatio { get; }
}
