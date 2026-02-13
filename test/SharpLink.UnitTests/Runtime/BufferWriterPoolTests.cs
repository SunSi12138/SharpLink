using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class BufferWriterPoolTests
{
    private static readonly Lock PoolSync = new();

    [Test]
    public void ConfigureShouldThrowOnNullConfigure()
    {
        AssertThrows<ArgumentNullException>(() => BufferWriterPool.Configure(null!));
    }

    [Test]
    public void ConfigureShouldThrowOnInvalidValues()
    {
        AssertThrows<ArgumentOutOfRangeException>(() =>
            BufferWriterPool.Configure(options => options.InitialCapacity = 0));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            BufferWriterPool.Configure(options => options.MaxPooledWriters = 0));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            BufferWriterPool.Configure(options => options.MaxRetainedCapacityBytes = 0));
    }

    [Test]
    public void ReturnShouldPoolSmallWriterAndClearContent()
    {
        lock (PoolSync)
        {
            try
            {
                ConfigurePool(initialCapacity: 111, maxPooledWriters: 1, maxRetainedCapacityBytes: 1024);
                DrainPool();

                var writer = new ArrayBufferWriter<byte>(128);
                writer.GetSpan(4);
                writer.Advance(4);
                BufferWriterPool.Return(writer);
                Ensure(writer.WrittenCount == 0, "writer should be cleared on return");
            }
            finally
            {
                ResetDefaults();
            }
        }
    }

    [Test]
    public void ReturnShouldDropWriterAboveRetainedCapacity()
    {
        lock (PoolSync)
        {
            try
            {
                ConfigurePool(initialCapacity: 123, maxPooledWriters: 1, maxRetainedCapacityBytes: 64);
                DrainPool();

                var tooLarge = new ArrayBufferWriter<byte>(256);
                tooLarge.GetSpan(1);
                tooLarge.Advance(1);
                BufferWriterPool.Return(tooLarge);
                Ensure(tooLarge.WrittenCount == 1, "oversized writer should not be cleared because it is not pooled");

                var rented = BufferWriterPool.Get();
                Ensure(!ReferenceEquals(tooLarge, rented), "oversized writer should not be pooled");
                Ensure(rented.Capacity == 123, "pool should allocate using configured initial capacity");
            }
            finally
            {
                ResetDefaults();
            }
        }
    }

    private static void DrainPool()
    {
        for (var i = 0; i < 8; i++)
        {
            _ = BufferWriterPool.Get();
        }
    }

    private static void ResetDefaults()
    {
        ConfigurePool(initialCapacity: 1024, maxPooledWriters: 512, maxRetainedCapacityBytes: 64 * 1024);
    }

    private static void ConfigurePool(int initialCapacity, int maxPooledWriters, int maxRetainedCapacityBytes)
    {
        BufferWriterPool.Configure(options =>
        {
            options.InitialCapacity = initialCapacity;
            options.MaxPooledWriters = maxPooledWriters;
            options.MaxRetainedCapacityBytes = maxRetainedCapacityBytes;
        });
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
