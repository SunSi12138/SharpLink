namespace SharpLink.UnitTests.Runtime;

public class BufferWriterPoolTests
{
    [Test]
    [NotInParallel]
    public void ConcurrentReturnsMustNotPopulateDetachedQueueAfterDispose()
    {
        const int writerCount = 256;
        var poolField = typeof(SharpLinkBufferWriterPool).GetField(
            "_pool",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("cannot inspect writer pool queue");
        var detachedWriters = 0;

        for (var iteration = 0; iteration < 500 && detachedWriters == 0; iteration++)
        {
            var pool = CreatePool(options =>
            {
                options.InitialCapacity = 16;
                options.MaxPooledWriters = writerCount;
                options.MaxRetainedCapacityBytes = 256;
            });
            var writers = new IRpcByteBufferWriter[writerCount];
            for (var index = 0; index < writers.Length; index++)
                writers[index] = pool.Rent();
            var queue = (System.Collections.Concurrent.ConcurrentQueue<PooledByteBufferWriter>)
                (poolField.GetValue(pool) ?? throw new Exception("writer pool queue disappeared before disposal"));

            Parallel.For(0, writerCount + 1, index =>
            {
                if (index == writerCount / 2)
                    pool.Dispose();
                else
                    pool.Return(writers[index < writerCount / 2 ? index : index - 1]);
            });

            detachedWriters = queue.Count;
            while (queue.TryDequeue(out var leaked))
                leaked.ReleaseRetainedBuffer();
            pool.Dispose();
        }

        Ensure(detachedWriters == 0,
            $"Dispose left {detachedWriters} writer(s) in a detached queue");
    }

    [Test]
    public void ConstructorShouldRejectNullOrInvalidOptions()
    {
        AssertThrows<ArgumentNullException>(() => _ = new SharpLinkBufferWriterPool(null!));
        AssertThrows<ArgumentOutOfRangeException>(() => CreatePool(options => options.InitialCapacity = 0));
        AssertThrows<ArgumentOutOfRangeException>(() => CreatePool(options => options.MaxPooledWriters = 0));
        AssertThrows<ArgumentOutOfRangeException>(() => CreatePool(options => options.MaxRetainedCapacityBytes = 0));
        AssertThrows<ArgumentOutOfRangeException>(() => CreatePool(options =>
            options.MaxRetainedCapacityBytes = BufferWriterPoolOptions.MaximumRetainedCapacityBytes + 1));
    }

    [Test]
    public void ReturnShouldReuseBoundedWriterStorage()
    {
        var pool = CreatePool(options =>
        {
            options.InitialCapacity = 111;
            options.MaxPooledWriters = 1;
            options.MaxRetainedCapacityBytes = 1024;
        });
        var writer = pool.Rent();
        writer.GetSpan(4);
        writer.Advance(4);

        pool.Return(writer);
        AssertThrows<ObjectDisposedException>(() => _ = writer.WrittenCount);
        var rented = pool.Rent();

        Ensure(ReferenceEquals(writer, rented), "the allocation-free writer shell should be reused");
        Ensure(rented.WrittenCount == 0, "a new lease should not expose bytes from its previous use");
        Ensure(rented.Capacity >= 111, "the new array lease should honor the configured initial capacity");
        pool.Return(rented);
    }

    [Test]
    public void LargeLeaseShouldReturnStorageBeforeWriterShellIsReused()
    {
        var pool = CreatePool(options =>
        {
            options.InitialCapacity = 123;
            options.MaxPooledWriters = 1;
            options.MaxRetainedCapacityBytes = 64;
        });
        var tooLarge = pool.Rent();
        tooLarge.GetSpan(256);
        tooLarge.Advance(256);

        pool.Return(tooLarge);
        var rented = pool.Rent();

        Ensure(ReferenceEquals(tooLarge, rented), "large storage must not prevent reuse of the writer shell");
        Ensure(rented.WrittenCount == 0, "large frame bytes must not survive into the next lease");
        Ensure(rented.Capacity < 256, "the large backing array must have been returned to ArrayPool");
        pool.Return(rented);
    }

    [Test]
    public void PooledWriterShouldGrowPreserveBytesAndDisposeIdempotently()
    {
        var writer = new PooledByteBufferWriter(16);
        var first = writer.GetSpan(16);
        for (var index = 0; index < 16; index++)
            first[index] = (byte)index;
        writer.Advance(16);

        writer.GetSpan(80);
        Ensure(writer.Capacity >= 96, "growth should satisfy the requested contiguous capacity");
        for (var index = 0; index < 16; index++)
            Ensure(writer.WrittenSpan[index] == (byte)index, "growth must preserve written bytes");

        writer.Dispose();
        writer.Dispose();
        AssertThrows<ObjectDisposedException>(() => writer.GetSpan());
    }

    [Test]
    public void BoundedLeaseShouldRejectGrowthAndRemainReusable()
    {
        var pool = CreatePool(options => options.InitialCapacity = 16);
        var writer = pool.Rent(32);
        writer.GetSpan(32).Clear();
        writer.Advance(32);

        var limit = AssertThrows<SharpLinkException>(() => writer.GetSpan(1));
        Ensure(limit.Code == SharpLinkErrorCode.ResourceExhausted, "bounded writer error code");
        AssertThrows<SharpLinkException>(() => writer.Advance(1));
        pool.Return(writer);

        var reused = pool.Rent();
        reused.GetSpan(64).Clear();
        reused.Advance(64);
        Ensure(reused.WrittenCount == 64, "a later unbounded lease must not inherit the prior limit");
        pool.Return(reused);
    }

    private static SharpLinkBufferWriterPool CreatePool(Action<BufferWriterPoolOptions> configure)
    {
        var options = new BufferWriterPoolOptions();
        configure(options);
        return new SharpLinkBufferWriterPool(options);
    }

    private static TException AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
