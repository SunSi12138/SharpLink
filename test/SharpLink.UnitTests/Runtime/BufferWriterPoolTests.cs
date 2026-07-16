namespace SharpLink.UnitTests.Runtime;

public class BufferWriterPoolTests
{
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

    private static SharpLinkBufferWriterPool CreatePool(Action<BufferWriterPoolOptions> configure)
    {
        var options = new BufferWriterPoolOptions();
        configure(options);
        return new SharpLinkBufferWriterPool(options);
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
