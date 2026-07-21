namespace SharpLink.Client;

/// <summary>Configures the built-in retry policy for idempotent unary calls.</summary>
/// <remarks>
/// <see cref="MaxAttempts"/> is the total number of attempts, including the first attempt. Retry is
/// disabled until <see cref="SharpClientBuilder.UseRetry()"/> or an overload is called.
/// </remarks>
public sealed class SharpLinkRetryOptions
{
    /// <summary>Gets or sets the total attempt limit from one through ten. The default is three.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Gets or sets the initial non-negative retry delay. The default is 50 ms.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets or sets the non-negative upper bound for exponential retry delays. The default is 200 ms.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets or sets symmetric delay jitter from zero through one. The default is 0.2.</summary>
    public double JitterRatio { get; set; } = 0.2;

    internal SharpLinkRetryOptions CloneValidated()
    {
        if (MaxAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (InitialBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
        if (MaxBackoff < TimeSpan.Zero || MaxBackoff < InitialBackoff)
            throw new ArgumentOutOfRangeException(nameof(MaxBackoff));
        if (JitterRatio is < 0 or > 1 || double.IsNaN(JitterRatio))
            throw new ArgumentOutOfRangeException(nameof(JitterRatio));

        return new SharpLinkRetryOptions
        {
            MaxAttempts = MaxAttempts,
            InitialBackoff = InitialBackoff,
            MaxBackoff = MaxBackoff,
            JitterRatio = JitterRatio
        };
    }
}
