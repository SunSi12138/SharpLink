namespace SharpLink.Runtime;

internal sealed class Int32Codec : IRpcCodec<int>
{
    internal static readonly Int32Codec Instance = new();
    private const int Size = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in int value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableInt32Codec : IRpcCodec<int?>
{
    internal static readonly NullableInt32Codec Instance = new();
    private const int Size = 5; // 1 Tag + 4 Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in int? value, IBufferWriter<byte> writer)
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
    public int? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);

        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref tempStart, 1));
    }
}
