namespace SharpLink.Runtime;

/// <summary>Instance-scoped pool for packet writers.</summary>
public sealed class SharpLinkBufferWriterPool : IRpcBufferWriterPool
{
    private readonly ConcurrentQueue<PooledByteBufferWriter> _pool = [];
    private readonly int _initialCapacity;
    private readonly int _maxPooledWriters;
    private readonly int _maxRetainedCapacityBytes;
    private int _pooledCount;

    /// <summary>Gets the minimum array capacity rented for each new writer lease.</summary>
    public int InitialCapacity => _initialCapacity;

    /// <summary>Creates a pool from a validated immutable option snapshot.</summary>
    /// <param name="options">Pool capacity and retention limits.</param>
    public SharpLinkBufferWriterPool(BufferWriterPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options.CloneValidated();
        _initialCapacity = snapshot.InitialCapacity;
        _maxPooledWriters = snapshot.MaxPooledWriters;
        _maxRetainedCapacityBytes = snapshot.MaxRetainedCapacityBytes;
    }

    /// <inheritdoc />
    public IRpcByteBufferWriter Rent()
        => RentCore(int.MaxValue);

    /// <inheritdoc />
    public IRpcByteBufferWriter Rent(int maxWrittenBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWrittenBytes);
        return RentCore(maxWrittenBytes);
    }

    private IRpcByteBufferWriter RentCore(int maxWrittenBytes)
    {
        if (!_pool.TryDequeue(out var writer))
            writer = PooledByteBufferWriter.CreateInactive();
        else
            Interlocked.Decrement(ref _pooledCount);

        writer.Activate(Math.Min(_initialCapacity, maxWrittenBytes), maxWrittenBytes);
        return writer;
    }

    /// <inheritdoc />
    public void Return(IRpcByteBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (writer is not PooledByteBufferWriter pooledWriter)
        {
            writer.Dispose();
            return;
        }
        if (!pooledWriter.TryReturnToPool(_maxRetainedCapacityBytes))
            return;
        while (true)
        {
            var current = Volatile.Read(ref _pooledCount);
            if (current >= _maxPooledWriters)
            {
                pooledWriter.ReleaseRetainedBuffer();
                return;
            }
            if (Interlocked.CompareExchange(ref _pooledCount, current + 1, current) == current)
                break;
        }

        _pool.Enqueue(pooledWriter);
    }
}
