namespace SharpLink.Runtime;

/// <summary>Configures the writer pool owned by one runtime context.</summary>
public sealed class BufferWriterPoolOptions
{
    /// <summary>The hard upper bound for writers retained by SharpLink: 64 KiB.</summary>
    public const int MaximumRetainedCapacityBytes = 64 * 1024;

    /// <summary>Gets or sets the initial writer capacity.</summary>
    public int InitialCapacity { get; set; } = 1024;

    /// <summary>Gets or sets the maximum number of idle writers retained by this context.</summary>
    public int MaxPooledWriters { get; set; } = 512;

    /// <summary>Gets or sets the largest writer retained by this context.</summary>
    public int MaxRetainedCapacityBytes { get; set; } = 64 * 1024;

    /// <summary>Validates the configured limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxPooledWriters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxRetainedCapacityBytes, 0);
        if (MaxRetainedCapacityBytes > MaximumRetainedCapacityBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedCapacityBytes), $"Retained writers cannot exceed {MaximumRetainedCapacityBytes} bytes.");
    }

    /// <summary>Creates a validated copy isolated from later mutations.</summary>
    public BufferWriterPoolOptions CloneValidated()
    {
        Validate();
        return new BufferWriterPoolOptions
        {
            InitialCapacity = InitialCapacity,
            MaxPooledWriters = MaxPooledWriters,
            MaxRetainedCapacityBytes = MaxRetainedCapacityBytes
        };
    }
}
