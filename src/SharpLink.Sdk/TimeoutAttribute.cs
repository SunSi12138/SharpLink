namespace SharpLink.Sdk;

/// <summary>Defines the generated default timeout for an RPC method.</summary>
/// <remarks>Explicit values must produce a positive finite <see cref="TimeSpan"/>; invalid attribute constants report SHARPLINK050 at compile time.</remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TimeoutAttribute : Attribute
{
    public TimeoutAttribute()
    {
        Seconds = null;
    }

    public TimeoutAttribute(double seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Timeout must be greater than zero.");

        Seconds = seconds;
    }

    public double? Seconds { get; }
}
