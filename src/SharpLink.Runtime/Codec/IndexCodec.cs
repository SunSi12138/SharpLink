namespace SharpLink.Runtime;

internal sealed class IndexCodec : IRpcCodec<Index>
{
    internal static readonly IndexCodec Instance = new();
    private const int Size = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Index value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Index Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<Index>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<Index>(ref MemoryMarshal.GetReference(temp));
    }
}
internal sealed class NullableIndexCodec : IRpcCodec<Index?>
{
    internal static readonly NullableIndexCodec Instance = new();
    private const int Size = 5; // 1 Tag + 4 Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Index? value, IBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Index? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(start)) return null;
            return Unsafe.ReadUnaligned<Index>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (!CodecHelpers.ReadNullablePresence(tempStart)) return null;
        return Unsafe.ReadUnaligned<Index>(ref Unsafe.Add(ref tempStart, 1));
    }
}
