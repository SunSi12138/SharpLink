namespace SharpLink.Abstractions;

/// <summary>Describes the currently published built-in endpoint circuit-breaker configuration.</summary>
/// <param name="Generation">The monotonically increasing endpoint-admission publication generation.</param>
/// <param name="Enabled">Whether the built-in circuit breaker is active.</param>
/// <param name="MinimumThroughput">The active minimum sample count, or zero when disabled.</param>
/// <param name="FailureRatio">The active failure ratio, or zero when disabled.</param>
/// <param name="SamplingDuration">The active rolling sampling duration.</param>
/// <param name="BreakDuration">The duration used by future Open transitions.</param>
/// <param name="HalfOpenMaxCalls">The active HalfOpen concurrency limit, or zero when disabled.</param>
public readonly record struct SharpLinkCircuitBreakerPolicySnapshot(
    ulong Generation,
    bool Enabled,
    int MinimumThroughput,
    double FailureRatio,
    TimeSpan SamplingDuration,
    TimeSpan BreakDuration,
    int HalfOpenMaxCalls);
