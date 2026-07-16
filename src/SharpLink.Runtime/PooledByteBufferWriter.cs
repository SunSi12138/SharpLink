namespace SharpLink.Runtime;

/// <summary>
/// A contiguous byte writer whose backing storage is rented from
/// <see cref="ArrayPool{T}"/> for one ownership lease.
/// </summary>
public sealed class PooledByteBufferWriter : IRpcByteBufferWriter
{
    private byte[]? _buffer;
    private int _written;
    private int _active;

    /// <summary>Creates an independently owned writer lease.</summary>
    /// <param name="initialCapacity">The minimum initial byte capacity.</param>
    /// <example><code>using var writer = new PooledByteBufferWriter(1024);</code></example>
    public PooledByteBufferWriter(int initialCapacity = 1024)
    {
        Activate(initialCapacity);
    }

    private PooledByteBufferWriter(bool inactive)
    {
        _ = inactive;
    }

    internal static PooledByteBufferWriter CreateInactive() => new(inactive: true);

    /// <inheritdoc />
    public int WrittenCount
    {
        get
        {
            EnsureActive();
            return _written;
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            EnsureActive();
            return _buffer.AsMemory(0, _written);
        }
    }

    /// <inheritdoc />
    public Span<byte> WrittenSpan
    {
        get
        {
            EnsureActive();
            return _buffer.AsSpan(0, _written);
        }
    }

    /// <inheritdoc />
    public int Capacity
    {
        get
        {
            EnsureActive();
            return _buffer!.Length;
        }
    }

    internal void Activate(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("The byte writer already has an active lease.");

        try
        {
            if (_buffer is null || _buffer.Length < initialCapacity)
            {
                var previous = _buffer;
                _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
                if (previous is not null)
                    ArrayPool<byte>.Shared.Return(previous);
            }
            _written = 0;
        }
        catch
        {
            Volatile.Write(ref _active, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer!.Length - _written)
            throw new InvalidOperationException("Cannot advance beyond the available capacity.");
        _written += count;
    }

    /// <inheritdoc />
    public void Clear()
    {
        EnsureActive();
        _written = 0;
    }

    /// <inheritdoc />
    public void Dispose() => TryRelease();

    internal bool TryRelease()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return false;

        var buffer = Interlocked.Exchange(ref _buffer, null);
        _written = 0;
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
        return true;
    }

    internal bool TryReturnToPool(int maxRetainedCapacityBytes)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return false;

        _written = 0;
        if (_buffer is { Length: var length } buffer && length > maxRetainedCapacityBytes)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return true;
    }

    internal void ReleaseRetainedBuffer()
    {
        if (Volatile.Read(ref _active) != 0)
            throw new InvalidOperationException("Cannot release storage from an active writer lease.");
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void EnsureCapacity(int sizeHint)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (sizeHint == 0)
            sizeHint = 1;
        if (sizeHint <= _buffer!.Length - _written)
            return;

        var required = checked(_written + sizeHint);
        var doubled = _buffer.Length <= int.MaxValue / 2 ? _buffer.Length * 2 : int.MaxValue;
        var newCapacity = Math.Max(required, doubled);
        var replacement = ArrayPool<byte>.Shared.Rent(newCapacity);
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        var previous = _buffer;
        _buffer = replacement;
        ArrayPool<byte>.Shared.Return(previous);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureActive()
    {
        if (Volatile.Read(ref _active) == 0)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
    }
}
