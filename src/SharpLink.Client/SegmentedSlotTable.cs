namespace SharpLink.Client;

/// <summary>
/// Provides bounded direct-index storage without eagerly allocating the full logical slot array.
/// Segments are published once and retained for the owning table lifetime, avoiding segment teardown ABA races.
/// A single immutable segment descriptor cache avoids repeating the root-directory lookup while request IDs stay
/// inside the same segment; cache misses always fall back to the authoritative root entry.
/// </summary>
internal sealed class SegmentedSlotTable<T> where T : class
{
    private const int MaximumSegmentSize = 256;

    private readonly Segment?[] _segments;
    private readonly int _segmentShift;
    private readonly int _segmentMask;
    private readonly int _segmentSize;
    private Segment? _cachedSegment;

    internal SegmentedSlotTable(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!System.Numerics.BitOperations.IsPow2(length))
            throw new ArgumentException("Segmented slot-table length must be a power of two.", nameof(length));

        Length = length;
        _segmentSize = Math.Min(length, MaximumSegmentSize);
        _segmentShift = System.Numerics.BitOperations.Log2((uint)_segmentSize);
        _segmentMask = _segmentSize - 1;
        _segments = new Segment?[length / _segmentSize];
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
        var segmentIndex = index >> _segmentShift;
        var segment = Volatile.Read(ref _cachedSegment);
        if (segment is null || segment.Index != segmentIndex)
        {
            segment = Volatile.Read(ref _segments[segmentIndex]);
            if (segment is null)
                return null;
            Volatile.Write(ref _cachedSegment, segment);
        }

        return Volatile.Read(ref segment.Slots[index & _segmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSegment(int index)
    {
        var segmentIndex = index >> _segmentShift;
        var segment = Volatile.Read(ref _cachedSegment);
        if (segment is not null && segment.Index == segmentIndex)
            return;

        segment = Volatile.Read(ref _segments[segmentIndex]);
        if (segment is null)
            segment = CreateSegmentSlow(segmentIndex);
        Volatile.Write(ref _cachedSegment, segment);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? CompareExchange(int index, T? value, T? comparand)
    {
        var segmentIndex = index >> _segmentShift;
        var segment = Volatile.Read(ref _cachedSegment);
        if (segment is null || segment.Index != segmentIndex)
        {
            segment = Volatile.Read(ref _segments[segmentIndex]);
            if (segment is null)
            {
                if (value is null)
                    return null;

                segment = CreateSegmentSlow(segmentIndex);
            }
            Volatile.Write(ref _cachedSegment, segment);
        }

        return Interlocked.CompareExchange(ref segment.Slots[index & _segmentMask], value, comparand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T?[]? GetMaterializedSegment(int segmentIndex)
        => Volatile.Read(ref _segments[segmentIndex])?.Slots;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Segment CreateSegmentSlow(int segmentIndex)
    {
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        if (segment is not null)
            return segment;

        var created = new Segment(segmentIndex, _segmentSize);
        return Interlocked.CompareExchange(ref _segments[segmentIndex], created, null) ?? created;
    }

    private sealed class Segment
    {
        internal Segment(int index, int size)
        {
            Index = index;
            Slots = new T?[size];
        }

        internal int Index { get; }

        internal T?[] Slots { get; }
    }
}
