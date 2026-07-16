namespace SharpLink.Runtime;

internal sealed class UInt64Codec : IRpcCodec<ulong>
{
    internal static readonly UInt64Codec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ulong value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableUInt64Codec : IRpcCodec<ulong?>
{
    internal static readonly NullableUInt64Codec Instance = new();
    private const int Size = 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ulong? value, IBufferWriter<byte> writer)
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
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0UL);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref tempStart, 1));
    }
}
