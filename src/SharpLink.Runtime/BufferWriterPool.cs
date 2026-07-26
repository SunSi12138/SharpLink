namespace SharpLink.Runtime;

/// <summary>Instance-scoped pool for packet writers.</summary>
public sealed class SharpLinkBufferWriterPool : IRpcBufferWriterPool, IDisposable
{
    private ConcurrentQueue<PooledByteBufferWriter>? _pool = [];
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
        var pool = Volatile.Read(ref _pool);
        ObjectDisposedException.ThrowIf(pool is null, this);
        if (!pool.TryDequeue(out var writer))
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
        var pool = Volatile.Read(ref _pool);
        if (pool is null)
        {
            pooledWriter.ReleaseRetainedBuffer();
            return;
        }
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

        pool.Enqueue(pooledWriter);
    }

    /// <summary>Releases every idle writer retained by this pool and rejects subsequent rents.</summary>
    public void Dispose()
    {
        var pool = Interlocked.Exchange(ref _pool, null);
        if (pool is null)
            return;
        DrainRetainedWriters(pool);
    }

    private void DrainRetainedWriters(ConcurrentQueue<PooledByteBufferWriter> pool)
    {
        while (pool.TryDequeue(out var writer))
        {
            Interlocked.Decrement(ref _pooledCount);
            writer.ReleaseRetainedBuffer();
        }
    }
}
