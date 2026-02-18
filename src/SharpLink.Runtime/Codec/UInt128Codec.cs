namespace SharpLink.Runtime;

internal sealed class UInt128Codec : IRpcCodec<UInt128>
{
    internal static readonly UInt128Codec Instance = new();
    private const int Size = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in UInt128 value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UInt128 Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<UInt128>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<UInt128>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableUInt128Codec : IRpcCodec<UInt128?>
{
    internal static readonly NullableUInt128Codec Instance = new();
    private const int Size = 17;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in UInt128? value, in ArrayBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), default(UInt128));
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UInt128? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<UInt128>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<UInt128>(ref Unsafe.Add(ref tempStart, 1));
    }
}