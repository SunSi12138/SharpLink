namespace SharpLink.Sdk;

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
