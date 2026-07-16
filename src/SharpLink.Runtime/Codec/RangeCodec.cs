namespace SharpLink.Runtime;

internal sealed class RangeCodec : IRpcCodec<Range>
{
    internal static readonly RangeCodec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Range value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Range Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<Range>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<Range>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableRangeCodec : IRpcCodec<Range?>
{
    internal static readonly NullableRangeCodec Instance = new();
    private const int Size = 9; // 1 Tag + 8 Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Range? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), value.GetValueOrDefault());
        }
        else
        {
            start = 0;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0L);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Range? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<Range>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);

        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<Range>(ref Unsafe.Add(ref tempStart, 1));
    }
}
