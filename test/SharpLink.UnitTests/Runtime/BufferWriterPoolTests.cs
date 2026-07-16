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
    public void ReturnShouldPoolSmallWriterAndClearContent()
    {
        var pool = CreatePool(options =>
        {
            options.InitialCapacity = 111;
            options.MaxPooledWriters = 1;
            options.MaxRetainedCapacityBytes = 1024;
        });
        var writer = new ArrayBufferWriter<byte>(128);
        writer.GetSpan(4);
        writer.Advance(4);

        pool.Return(writer);
        var rented = pool.Rent();

        Ensure(writer.WrittenCount == 0, "writer should be cleared on return");
        Ensure(ReferenceEquals(writer, rented), "small writer should be retained by its context pool");
    }

    [Test]
    public void ReturnShouldDropWriterAboveRetainedCapacity()
    {
        var pool = CreatePool(options =>
        {
            options.InitialCapacity = 123;
            options.MaxPooledWriters = 1;
            options.MaxRetainedCapacityBytes = 64;
        });
        var tooLarge = new ArrayBufferWriter<byte>(256);
        tooLarge.GetSpan(1);
        tooLarge.Advance(1);

        pool.Return(tooLarge);
        var rented = pool.Rent();

        Ensure(tooLarge.WrittenCount == 1, "oversized writer should not be retained or mutated");
        Ensure(!ReferenceEquals(tooLarge, rented), "oversized writer should not be pooled");
        Ensure(rented.Capacity == 123, "new writer should use the context snapshot");
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
