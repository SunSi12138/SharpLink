namespace SharpLink.Client;

/// <summary>
/// Provides bounded direct-index storage without eagerly allocating the full logical slot array.
/// Segments are published once and retained for the table lifetime; request-ID allocation keeps
/// low-concurrency churn inside the already-materialized high-water working set.
/// </summary>
internal sealed class SegmentedSlotTable<T> where T : class
{
    private const int MaximumSegmentSize = 256;

    private readonly T?[]?[] _segments;
    private readonly int _segmentShift;
    private readonly int _segmentMask;
    private readonly int _segmentSize;
    private int _materializedSegmentCount;

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

    internal int MaterializedSegmentCount => Volatile.Read(ref _materializedSegmentCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        return segment is null ? null : Volatile.Read(ref segment[index & _segmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index, out bool segmentMaterialized)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        if (segment is null)
        {
            segmentMaterialized = false;
            return null;
        }

        segmentMaterialized = true;
        return Volatile.Read(ref segment[index & _segmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsSegmentMaterialized(int index)
        => Volatile.Read(ref _segments[index >> _segmentShift]) is not null;

    internal bool TryGetFirstMaterializedIndex(out int index)
    {
        for (var segmentIndex = 0; segmentIndex < _segments.Length; segmentIndex++)
        {
            if (Volatile.Read(ref _segments[segmentIndex]) is null)
                continue;

            index = segmentIndex << _segmentShift;
            return true;
        }

        index = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSegment(int index)
    {
        var segmentIndex = index >> _segmentShift;
        if (Volatile.Read(ref _segments[segmentIndex]) is null)
            _ = CreateSegmentSlow(segmentIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? CompareExchange(int index, T? value, T? comparand)
    {
        var segment = Volatile.Read(ref _segments[index >> _segmentShift]);
        if (segment is null)
        {
            if (value is null)
                return null;

            segment = CreateSegmentSlow(index >> _segmentShift);
        }
        return Interlocked.CompareExchange(ref segment[index & _segmentMask], value, comparand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T?[]? GetMaterializedSegment(int segmentIndex)
        => Volatile.Read(ref _segments[segmentIndex]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T?[] CreateSegmentSlow(int segmentIndex)
    {
        var created = new T?[_segmentSize];
        var existing = Interlocked.CompareExchange(ref _segments[segmentIndex], created, null);
        if (existing is not null)
            return existing;

        Interlocked.Increment(ref _materializedSegmentCount);
        return created;
    }
}
