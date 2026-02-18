namespace SharpLink.Runtime;

public readonly record struct RpcSessionFlushOptions(int FlushSizeThreshold, TimeSpan MaxLatency)
{
    public static RpcSessionFlushOptions Default => new(4 * 1024, TimeSpan.FromMilliseconds(1));

    public static RpcSessionFlushOptions Create(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Validate(flushSizeThreshold, maxLatency);
        return new RpcSessionFlushOptions(flushSizeThreshold, maxLatency);
    }

    public static void Validate(int flushSizeThreshold, TimeSpan maxLatency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushSizeThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLatency, TimeSpan.Zero);
    }
}

public interface IRpcSessionFlushConfigurableTransport
{
    void ConfigureRpcSessionFlush(RpcSessionFlushOptions options);
}
