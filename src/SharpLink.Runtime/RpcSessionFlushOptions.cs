namespace SharpLink.Runtime;

/// <summary>Overrides the profile-derived session batching threshold and maximum batching delay.</summary>
public readonly record struct RpcSessionFlushOptions(int FlushSizeThreshold, TimeSpan MaxLatency)
{
    /// <summary>Gets the compatibility default used by explicitly configured sessions.</summary>
    public static RpcSessionFlushOptions Default => new(16 * 1024, TimeSpan.FromMilliseconds(1));

    /// <summary>Creates and validates an explicit timed batching policy.</summary>
    public static RpcSessionFlushOptions Create(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Validate(flushSizeThreshold, maxLatency);
        return new RpcSessionFlushOptions(flushSizeThreshold, maxLatency);
    }

    /// <summary>Validates an explicit timed batching policy.</summary>
    public static void Validate(int flushSizeThreshold, TimeSpan maxLatency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushSizeThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLatency, TimeSpan.Zero);
    }
}
