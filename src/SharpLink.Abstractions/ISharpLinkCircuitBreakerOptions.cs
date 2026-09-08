namespace SharpLink.Abstractions;

/// <summary>Provides the endpoint-generation circuit-breaker settings copied for runtime publication.</summary>
public interface ISharpLinkCircuitBreakerOptions
{
    /// <summary>Gets the minimum number of samples required before the breaker may open.</summary>
    int MinimumThroughput { get; }

    /// <summary>Gets the failure ratio from greater than zero through one that opens the breaker.</summary>
    double FailureRatio { get; }

    /// <summary>Gets the rolling sample-window duration.</summary>
    TimeSpan SamplingDuration { get; }

    /// <summary>Gets the duration assigned to future Closed/HalfOpen to Open transitions.</summary>
    TimeSpan BreakDuration { get; }

    /// <summary>Gets the maximum number of concurrently admitted HalfOpen probes.</summary>
    int HalfOpenMaxCalls { get; }
}
