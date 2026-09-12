namespace SharpLink.Runtime;

internal sealed class UInt32Codec : IRpcCodec<uint>
{
    internal static readonly UInt32Codec Instance = new();
    private const int Size = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in uint value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableUInt32Codec : IRpcCodec<uint?>
{
    internal static readonly NullableUInt32Codec Instance = new();
    private const int Size = 5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in uint? value, IBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0U);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);

        if (!CodecHelpers.ReadNullablePresence(ref tempStart, Size - 1)) return null;
        return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref tempStart, 1));
    }
}
