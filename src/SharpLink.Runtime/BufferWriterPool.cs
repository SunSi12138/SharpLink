namespace SharpLink.Runtime;

public static class BufferWriterPool
{
    private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> Pool = [];
    private static int _initialCapacity = 1024;
    private static int _maxPooledWriters = 512;
    private static int _maxRetainedCapacityBytes = 64 * 1024;
    private static int _pooledCount;

    public static void Configure(Action<BufferWriterPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BufferWriterPoolOptions
        {
            InitialCapacity = Volatile.Read(ref _initialCapacity),
            MaxPooledWriters = Volatile.Read(ref _maxPooledWriters),
            MaxRetainedCapacityBytes = Volatile.Read(ref _maxRetainedCapacityBytes)
        };

        configure(options);
        options.Validate();

        Interlocked.Exchange(ref _initialCapacity, options.InitialCapacity);
        Interlocked.Exchange(ref _maxPooledWriters, options.MaxPooledWriters);
        Interlocked.Exchange(ref _maxRetainedCapacityBytes, options.MaxRetainedCapacityBytes);

        TrimPoolIfNeeded();
    }

    public static ArrayBufferWriter<byte> Get()
    {
        if (!Pool.TryDequeue(out var writer))
            return new ArrayBufferWriter<byte>(Volatile.Read(ref _initialCapacity));

        Interlocked.Decrement(ref _pooledCount);
        return writer;
    }

    public static void Return(ArrayBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (writer.Capacity > Volatile.Read(ref _maxRetainedCapacityBytes))
            return;

        writer.Clear();
        while (true)
        {
            var current = Volatile.Read(ref _pooledCount);
            if (current >= Volatile.Read(ref _maxPooledWriters))
                return;

            if (Interlocked.CompareExchange(ref _pooledCount, current + 1, current) != current)
                continue;

            Pool.Enqueue(writer);
            return;
        }
    }

    private static void TrimPoolIfNeeded()
    {
        while (Volatile.Read(ref _pooledCount) > Volatile.Read(ref _maxPooledWriters) && Pool.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pooledCount);
        }
    }
}
