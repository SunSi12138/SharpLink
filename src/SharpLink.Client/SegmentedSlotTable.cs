namespace SharpLink.Client;

/// <summary>
/// Provides bounded direct-index storage without eagerly allocating the full logical slot array.
/// Segments are published once and retained for the table lifetime; request-ID allocation keeps
/// low-concurrency churn inside the already-materialized high-water working set. The first segment
/// remains lazy but is stored directly so the common low-concurrency path avoids a directory hop.
/// </summary>
internal sealed class SegmentedSlotTable<T> where T : class
{
    private const int MaximumSegmentSize = 256;
    private const int MaximumSegmentShift = 8;
    private const int MaximumSegmentMask = MaximumSegmentSize - 1;

    private readonly T?[]?[] _secondarySegments;
    private readonly int _segmentSize;
    private T?[]? _firstSegment;
    private int _materializedSegmentCount;

    internal SegmentedSlotTable(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!System.Numerics.BitOperations.IsPow2(length))
            throw new ArgumentException("Segmented slot-table length must be a power of two.", nameof(length));

        Length = length;
        _segmentSize = Math.Min(length, MaximumSegmentSize);
        _secondarySegments = new T?[]?[Math.Max(0, (length / _segmentSize) - 1)];
    }

    internal int Length { get; }

    internal int SegmentCount => _secondarySegments.Length + 1;

    internal int SegmentSize => _segmentSize;

    internal int MaterializedSegmentCount => Volatile.Read(ref _materializedSegmentCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index)
    {
        if ((uint)index < MaximumSegmentSize)
        {
            var first = Volatile.Read(ref _firstSegment);
            return first is null ? null : Volatile.Read(ref first[index]);
        }

        var segment = Volatile.Read(ref _secondarySegments[(index >> MaximumSegmentShift) - 1]);
        return segment is null ? null : Volatile.Read(ref segment[index & MaximumSegmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Read(int index, out bool segmentMaterialized)
    {
        if ((uint)index < MaximumSegmentSize)
        {
            var first = Volatile.Read(ref _firstSegment);
            if (first is null)
            {
                segmentMaterialized = false;
                return null;
            }

            segmentMaterialized = true;
            return Volatile.Read(ref first[index]);
        }

        var segment = Volatile.Read(ref _secondarySegments[(index >> MaximumSegmentShift) - 1]);
        if (segment is null)
        {
            segmentMaterialized = false;
            return null;
        }

        segmentMaterialized = true;
        return Volatile.Read(ref segment[index & MaximumSegmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsSegmentMaterialized(int index)
    {
        if ((uint)index < MaximumSegmentSize)
            return Volatile.Read(ref _firstSegment) is not null;
        return Volatile.Read(ref _secondarySegments[(index >> MaximumSegmentShift) - 1]) is not null;
    }

    internal bool TryGetFirstMaterializedIndex(out int index)
    {
        if (Volatile.Read(ref _firstSegment) is not null)
        {
            index = 0;
            return true;
        }

        for (var secondaryIndex = 0; secondaryIndex < _secondarySegments.Length; secondaryIndex++)
        {
            if (Volatile.Read(ref _secondarySegments[secondaryIndex]) is null)
                continue;

            index = (secondaryIndex + 1) << MaximumSegmentShift;
            return true;
        }

        index = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSegment(int index)
    {
        if ((uint)index < MaximumSegmentSize)
        {
            if (Volatile.Read(ref _firstSegment) is null)
                _ = CreateSegmentSlow(segmentIndex: 0);
            return;
        }

        var secondaryIndex = (index >> MaximumSegmentShift) - 1;
        if (Volatile.Read(ref _secondarySegments[secondaryIndex]) is null)
            _ = CreateSegmentSlow(secondaryIndex + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? CompareExchange(int index, T? value, T? comparand)
    {
        if ((uint)index < MaximumSegmentSize)
        {
            var first = Volatile.Read(ref _firstSegment);
            if (first is null)
            {
                if (value is null)
                    return null;

                first = CreateSegmentSlow(segmentIndex: 0);
            }
            return Interlocked.CompareExchange(ref first[index], value, comparand);
        }

        var segmentIndex = index >> MaximumSegmentShift;
        var segment = Volatile.Read(ref _secondarySegments[segmentIndex - 1]);
        if (segment is null)
        {
            if (value is null)
                return null;

            segment = CreateSegmentSlow(segmentIndex);
        }
        return Interlocked.CompareExchange(ref segment[index & MaximumSegmentMask], value, comparand);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T?[]? GetMaterializedSegment(int segmentIndex)
        => segmentIndex == 0
            ? Volatile.Read(ref _firstSegment)
            : Volatile.Read(ref _secondarySegments[segmentIndex - 1]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T?[] CreateSegmentSlow(int segmentIndex)
    {
        var created = new T?[_segmentSize];
        T?[]? existing;
        if (segmentIndex == 0)
            existing = Interlocked.CompareExchange(ref _firstSegment, created, null);
        else
            existing = Interlocked.CompareExchange(ref _secondarySegments[segmentIndex - 1], created, null);

        if (existing is not null)
            return existing;

        Interlocked.Increment(ref _materializedSegmentCount);
        return created;
    }
}
