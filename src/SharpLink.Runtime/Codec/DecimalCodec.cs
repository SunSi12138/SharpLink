namespace SharpLink.Runtime;

internal sealed class DecimalCodec : IRpcCodec<decimal>
{
    internal static readonly DecimalCodec Instance = new();
    private const int Size = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in decimal value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<decimal>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<decimal>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableDecimalCodec : IRpcCodec<decimal?>
{
    internal static readonly NullableDecimalCodec Instance = new();
    private const int Size = 17;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in decimal? value, in ArrayBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), default(decimal));
        }
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            
            return Unsafe.ReadUnaligned<decimal>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (tempStart == 0) return null;

        return Unsafe.ReadUnaligned<decimal>(ref Unsafe.Add(ref tempStart, 1));
    }
}