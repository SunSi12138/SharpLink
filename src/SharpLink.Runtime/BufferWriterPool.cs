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

/// <summary>Legacy process-wide writer pool retained only for source compatibility.</summary>
public static class BufferWriterPool
{
    private static readonly Lock Gate = new();
    private static BufferWriterPoolOptions _options = new();
    private static SharpLinkBufferWriterPool _pool = new(_options);

    /// <summary>Configures only the legacy compatibility pool.</summary>
    [Obsolete("Use builder.UseBufferWriterPool; built clients and servers own independent pools.")]
    public static void Configure(Action<BufferWriterPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        lock (Gate)
        {
            var options = _options.CloneValidated();
            configure(options);
            options.Validate();
            _options = options;
            _pool = new SharpLinkBufferWriterPool(options);
        }
    }

    /// <summary>Rents from the legacy compatibility pool.</summary>
    public static IRpcByteBufferWriter Get() => Volatile.Read(ref _pool).Rent();

    /// <summary>Returns to the legacy compatibility pool.</summary>
    public static void Return(IRpcByteBufferWriter writer) => Volatile.Read(ref _pool).Return(writer);
}
