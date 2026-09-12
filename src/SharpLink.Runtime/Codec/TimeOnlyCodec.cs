namespace SharpLink.Runtime;

internal sealed class TimeOnlyCodec : IRpcCodec<TimeOnly>
{
    internal static readonly TimeOnlyCodec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in TimeOnly value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeOnly Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            return CodecHelpers.ValidateTimeOnly(
                Unsafe.ReadUnaligned<TimeOnly>(ref MemoryMarshal.GetReference(buffer.FirstSpan)));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return CodecHelpers.ValidateTimeOnly(
            Unsafe.ReadUnaligned<TimeOnly>(ref MemoryMarshal.GetReference(temp)));
    }
}

internal sealed class NullableTimeOnlyCodec : IRpcCodec<TimeOnly?>
{
    internal static readonly NullableTimeOnlyCodec Instance = new();
    private const int Size = 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in TimeOnly? value, IBufferWriter<byte> writer)
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
    public TimeOnly? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return CodecHelpers.ValidateTimeOnly(
                Unsafe.ReadUnaligned<TimeOnly>(ref Unsafe.Add(ref start, 1)));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);

        if (!CodecHelpers.ReadNullablePresence(ref tempStart, Size - 1)) return null;
        return CodecHelpers.ValidateTimeOnly(
            Unsafe.ReadUnaligned<TimeOnly>(ref Unsafe.Add(ref tempStart, 1)));
    }
}
