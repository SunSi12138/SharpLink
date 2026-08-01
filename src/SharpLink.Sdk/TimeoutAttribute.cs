namespace SharpLink.Sdk;

/// <summary>Defines the generated default timeout for an RPC method.</summary>
/// <remarks>Explicit values must produce a positive finite <see cref="TimeSpan"/>; invalid attribute constants report SHARPLINK050 at compile time.</remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TimeoutAttribute : Attribute
{
    /// <summary>Creates an attribute that uses the generated default timeout value.</summary>
    public TimeoutAttribute()
    {
        Seconds = null;
    }

    /// <summary>Creates an attribute with an explicit positive timeout.</summary>
    /// <param name="seconds">The finite timeout in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is not positive.</exception>
    public TimeoutAttribute(double seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Timeout must be greater than zero.");

        Seconds = seconds;
    }

    /// <summary>Gets the explicit timeout in seconds, or <see langword="null"/> for the generated default.</summary>
    public double? Seconds { get; }
}
