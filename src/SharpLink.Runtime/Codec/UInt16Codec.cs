namespace SharpLink.Runtime;

internal sealed class UInt16Codec : IRpcCodec<ushort>
{
    internal static readonly UInt16Codec Instance = new();
    private const int Size = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ushort value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableUInt16Codec : IRpcCodec<ushort?>
{
    internal static readonly NullableUInt16Codec Instance = new();
    private const int Size = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ushort? value, in ArrayBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));
        if (value.HasValue)
        {
            start = 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), value.GetValueOrDefault());
        }
        else
        {
            // 写 1 byte Tag(0) + 2 bytes Value(0)
            // 无法用 int，可以写 ushort(0)
            start = 0;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), (ushort)0);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref tempStart, 1));
    }
}
