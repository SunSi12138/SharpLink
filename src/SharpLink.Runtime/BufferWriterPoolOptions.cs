namespace SharpLink.Runtime;

public sealed class BufferWriterPoolOptions
{
    public int InitialCapacity { get; set; } = 1024;
    public int MaxPooledWriters { get; set; } = 512;
    public int MaxRetainedCapacityBytes { get; set; } = 64 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxPooledWriters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxRetainedCapacityBytes, 0);
    }
}
