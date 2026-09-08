namespace SharpLink.Client;

/// <summary>Configures the built-in endpoint circuit breaker.</summary>
public sealed class SharpLinkCircuitBreakerOptions : ISharpLinkCircuitBreakerOptions
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
        => CopyValidated(this);

    internal static SharpLinkCircuitBreakerOptions CopyValidated(ISharpLinkCircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var minimumThroughput = options.MinimumThroughput;
        var failureRatio = options.FailureRatio;
        var samplingDuration = options.SamplingDuration;
        var breakDuration = options.BreakDuration;
        var halfOpenMaxCalls = options.HalfOpenMaxCalls;

        if (minimumThroughput is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumThroughput must be from one through 1024.");
        if (failureRatio is <= 0 or > 1 || double.IsNaN(failureRatio))
            throw new ArgumentOutOfRangeException(nameof(options), "FailureRatio must be greater than zero through one.");
        if (samplingDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "SamplingDuration must be positive.");
        if (breakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "BreakDuration must be positive.");
        if (halfOpenMaxCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "HalfOpenMaxCalls must be positive.");

        return new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = minimumThroughput,
            FailureRatio = failureRatio,
            SamplingDuration = samplingDuration,
            BreakDuration = breakDuration,
            HalfOpenMaxCalls = halfOpenMaxCalls
        };
    }
}
