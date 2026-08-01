namespace SharpLink.Client;

/// <summary>Configures the built-in endpoint circuit breaker.</summary>
public sealed class SharpLinkCircuitBreakerOptions
{
    /// <summary>Gets or sets the minimum samples required before a Closed breaker can open. The default is 20.</summary>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>Gets or sets the inclusive infrastructure-failure ratio that opens a Closed breaker. The default is 0.5.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Gets or sets the rolling sample window. The default is 30 seconds.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how long an Open breaker rejects attempts before HalfOpen probing. The default is 10 seconds.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum concurrent HalfOpen probes. The default is one.</summary>
    public int HalfOpenMaxCalls { get; set; } = 1;

    internal SharpLinkCircuitBreakerOptions CloneValidated()
    {
        if (MinimumThroughput is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MinimumThroughput));
        if (FailureRatio is <= 0 or > 1 || double.IsNaN(FailureRatio))
            throw new ArgumentOutOfRangeException(nameof(FailureRatio));
        if (SamplingDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SamplingDuration));
        if (BreakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BreakDuration));
        if (HalfOpenMaxCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(HalfOpenMaxCalls));

        return new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = MinimumThroughput,
            FailureRatio = FailureRatio,
            SamplingDuration = SamplingDuration,
            BreakDuration = BreakDuration,
            HalfOpenMaxCalls = HalfOpenMaxCalls
        };
    }
}
