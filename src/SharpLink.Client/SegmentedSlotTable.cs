namespace SharpLink.Client;

/// <summary>
/// Provides bounded direct-index storage without eagerly allocating the full logical slot array.
/// A small reuse window keeps recently touched empty segments attached; older empty segments are
/// reclaimed once the table grows past that window so cumulative request-ID churn cannot restore
/// the full eager backing array.
/// </summary>
internal sealed class SegmentedSlotTable<T> where T : class
{
    private const int MaximumSegmentSize = 256;
    private const int MaximumRetainedSegments = 8;

    private readonly T?[]?[] _segments;
    private readonly T?[] _trimSentinel = [];
    private readonly int _segmentShift;
    private readonly int _segmentMask;
    private readonly int _segmentSize;
    private int _materializedSegmentCount;
    private int _trimCursor;

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
        var segment = ReadSegment(index >> _segmentShift);
        return segment is null ? null : Volatile.Read(ref segment[index & _segmentMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSegment(int index)
    {
        var segmentIndex = index >> _segmentShift;
        if (ReadSegment(segmentIndex) is null)
            _ = CreateSegmentSlow(segmentIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? CompareExchange(int index, T? value, T? comparand)
    {
        var segmentIndex = index >> _segmentShift;
        var offset = index & _segmentMask;

        while (true)
        {
            var segment = ReadSegment(segmentIndex);
            if (segment is null)
            {
                if (value is null)
                    return null;

                segment = CreateSegmentSlow(segmentIndex);
            }

            var exchanged = Interlocked.CompareExchange(ref segment[offset], value, comparand);
            if (!ReferenceEquals(exchanged, comparand))
                return exchanged;

            if (value is null)
            {
                if (comparand is not null && MaterializedSegmentCount > MaximumRetainedSegments)
                    TryTrimOneEmptySegment(segmentIndex);
                return exchanged;
            }

            var publishedRoot = ReadSegment(segmentIndex);
            if (ReferenceEquals(publishedRoot, segment))
                return exchanged;

            // A trim can detach an empty segment after this publisher acquired the old array but
            // before the slot CAS. The value is not externally registered yet, so roll it back and
            // retry against the current root instead of publishing into unreachable storage.
            var rollback = Interlocked.CompareExchange(ref segment[offset], comparand, value);
            if (!ReferenceEquals(rollback, value))
            {
                throw new InvalidOperationException(
                    "A pending-slot publication changed after its segment was detached.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T?[]? GetMaterializedSegment(int segmentIndex)
        => ReadSegment(segmentIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T?[]? ReadSegment(int segmentIndex)
    {
        var segment = Volatile.Read(ref _segments[segmentIndex]);
        return ReferenceEquals(segment, _trimSentinel)
            ? ReadSegmentAfterTrim(segmentIndex)
            : segment;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T?[]? ReadSegmentAfterTrim(int segmentIndex)
    {
        var spinner = new SpinWait();
        while (true)
        {
            var segment = Volatile.Read(ref _segments[segmentIndex]);
            if (!ReferenceEquals(segment, _trimSentinel))
                return segment;
            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T?[] CreateSegmentSlow(int segmentIndex)
    {
        while (true)
        {
            var segment = ReadSegment(segmentIndex);
            if (segment is not null)
                return segment;

            var created = new T?[_segmentSize];
            Interlocked.Increment(ref _materializedSegmentCount);
            var existing = Interlocked.CompareExchange(ref _segments[segmentIndex], created, null);
            if (existing is null)
                return created;

            Interlocked.Decrement(ref _materializedSegmentCount);
            if (!ReferenceEquals(existing, _trimSentinel))
                return existing;
        }
    }

    private void TryTrimOneEmptySegment(int preserveSegmentIndex)
    {
        if (MaterializedSegmentCount <= MaximumRetainedSegments)
            return;

        var start = (int)((uint)Interlocked.Increment(ref _trimCursor) % (uint)_segments.Length);
        for (var attempt = 0; attempt < _segments.Length; attempt++)
        {
            var segmentIndex = (start + attempt) % _segments.Length;
            if (segmentIndex == preserveSegmentIndex)
                continue;

            if (TryDetachEmptySegment(segmentIndex))
                return;
        }

        _ = TryDetachEmptySegment(preserveSegmentIndex);
    }

    private bool TryDetachEmptySegment(int segmentIndex)
    {
        var segment = ReadSegment(segmentIndex);
        if (segment is null || !IsEmpty(segment))
            return false;

        if (!ReferenceEquals(
                Interlocked.CompareExchange(ref _segments[segmentIndex], _trimSentinel, segment),
                segment))
        {
            return false;
        }

        // The sentinel prevents new root-based readers/writers from committing while we validate
        // the candidate a second time. A publisher that raced using the old array waits for this
        // decision; if we detach, it observes null, rolls back its unpublished slot, and retries.
        if (!IsEmpty(segment))
        {
            Volatile.Write(ref _segments[segmentIndex], segment);
            return false;
        }

        Volatile.Write(ref _segments[segmentIndex], null);
        var remaining = Interlocked.Decrement(ref _materializedSegmentCount);
        if (remaining < 0)
            throw new InvalidOperationException("Pending-slot materialized segment count underflowed.");
        return true;
    }

    private static bool IsEmpty(T?[] segment)
    {
        for (var offset = 0; offset < segment.Length; offset++)
        {
            if (Volatile.Read(ref segment[offset]) is not null)
                return false;
        }
        return true;
    }
}
