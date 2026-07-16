namespace SharpLink.Runtime;

internal sealed class Int16Codec : IRpcCodec<short>
{
    internal static readonly Int16Codec Instance = new();
    private const int Size = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in short value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<short>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<short>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableInt16Codec : IRpcCodec<short?>
{
    internal static readonly NullableInt16Codec Instance = new();
    private const int Size = 3; // 1 byte tag + 2 bytes value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in short? value, in ArrayBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), (ushort)0);
        }

        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;

            return Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (tempStart == 0) return null;

        return Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref tempStart, 1));
    }
}
