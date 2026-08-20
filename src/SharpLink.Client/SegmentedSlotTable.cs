namespace SharpLink.Client;

/// <summary>
/// Provides bounded direct-index storage without eagerly allocating the full logical slot array.
/// Segments are published once and retained for the owning table lifetime, avoiding segment teardown ABA races.
/// </summary>
internal sealed class SegmentedSlotTable<T> where T : class
{
    private const int MaximumSegmentSize = 256;

    private readonly T?[]?[] _segments;
    private readonly int _segmentShift;
    private readonly int _segmentMask;
    private readonly int _segmentSize;

    internal SegmentedSlotTable(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!System.Numerics.BitOperations.IsPow2(length))
            throw new ArgumentException("Segmented slot-table length must be a power of two.", nameof(length));

        Length = length;
        _segmentSize = Math.Min(length, MaximumSegmentSize);
        _segmentShift = System.Numerics.BitOperations.Log2((uint)_segmentSize);
        _segmentMask = _segmentSize - 1;
        _segments = new T?[]?[length / _segmentSize];
    }

    internal int Length { get; }

    internal int SegmentCount => _segments.Length;

    internal int SegmentSize => _segmentSize;

    internal int MaterializedSegmentCount
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _segments.Length; index++)
            {
                if (Volatile.Read(ref _segments[index]) is not null)
                    count++;
            }
            return count;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        return segment is null ? null : Volatile.Read(ref segment[index & _segmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSegment(int index)
        => _ = GetOrCreateSegment(index >> _segmentShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? CompareExchange(int index, T? value, T? comparand)
    {
        var segmentIndex = index >> _segmentShift;
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        if (segment is null)
        {
            if (value is null)
                return null;

            segment = GetOrCreateSegment(segmentIndex);
        }

        return Interlocked.CompareExchange(ref segment[index & _segmentMask], value, comparand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T?[]? GetMaterializedSegment(int segmentIndex)
        => Volatile.Read(ref _segments[segmentIndex]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T?[] GetOrCreateSegment(int segmentIndex)
    {
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        if (segment is not null)
            return segment;

        var created = new T?[_segmentSize];
        return Interlocked.CompareExchange(ref _segments[segmentIndex], created, null) ?? created;
    }
}
