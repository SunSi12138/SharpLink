using System;
using System.Buffers;
using System.Collections.Generic;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal readonly record struct BufferGrowthTransition(
    int PreviousCapacity,
    int NewCapacity,
    int WrittenBytes,
    int SizeHint);

/// <summary>
/// Observes every capacity-changing writer request without modifying the production writer.
/// The copied byte count is exact because a growth copies every byte written before that request.
/// </summary>
internal sealed class GrowthTrackingBufferWriter(PooledByteBufferWriter writer) : IRpcByteBufferWriter
{
    private readonly List<BufferGrowthTransition> _transitions = [];

    public int GrowthCount => _transitions.Count;

    public long CopiedBytes { get; private set; }

    public IReadOnlyList<BufferGrowthTransition> Transitions => _transitions;

    public void Advance(int count) => writer.Advance(count);

    public int WrittenCount => writer.WrittenCount;

    public ReadOnlyMemory<byte> WrittenMemory => writer.WrittenMemory;

    public Span<byte> WrittenSpan => writer.WrittenSpan;

    public int Capacity => writer.Capacity;

    public void Clear() => writer.Clear();

    public void Dispose() => writer.Dispose();

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        var previousCapacity = writer.Capacity;
        var writtenBytes = writer.WrittenCount;
        var memory = writer.GetMemory(sizeHint);
        RecordGrowth(previousCapacity, writtenBytes, sizeHint);
        return memory;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        var previousCapacity = writer.Capacity;
        var writtenBytes = writer.WrittenCount;
        var span = writer.GetSpan(sizeHint);
        RecordGrowth(previousCapacity, writtenBytes, sizeHint);
        return span;
    }

    private void RecordGrowth(int previousCapacity, int writtenBytes, int sizeHint)
    {
        if (writer.Capacity == previousCapacity)
            return;

        _transitions.Add(new BufferGrowthTransition(
            previousCapacity,
            writer.Capacity,
            writtenBytes,
            sizeHint));
        CopiedBytes += writtenBytes;
    }
}
