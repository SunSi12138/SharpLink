namespace SharpLink.Runtime;

internal sealed class Int128Codec : IRpcCodec<Int128>
{
    internal static readonly Int128Codec Instance = new();
    private const int Size = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Int128 value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Int128 Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<Int128>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<Int128>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableInt128Codec : IRpcCodec<Int128?>
{
    internal static readonly NullableInt128Codec Instance = new();
    private const int Size = 17;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Int128? value, IBufferWriter<byte> writer)
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
            // Int128 默认值 0
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), default(Int128));
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Int128? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(start)) return null;
            return Unsafe.ReadUnaligned<Int128>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (!CodecHelpers.ReadNullablePresence(tempStart)) return null;
        return Unsafe.ReadUnaligned<Int128>(ref Unsafe.Add(ref tempStart, 1));
    }
}
