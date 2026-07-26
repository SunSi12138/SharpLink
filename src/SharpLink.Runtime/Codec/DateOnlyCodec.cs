namespace SharpLink.Runtime;

internal sealed class DateOnlyCodec : IRpcCodec<DateOnly>
{
    internal static readonly DateOnlyCodec Instance = new();
    private const int Size = 4; // DateOnly 内部是 int (DayNumber)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateOnly value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value.DayNumber);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateOnly Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        int dayNumber;
        if (buffer.FirstSpan.Length >= Size)
        {
            dayNumber = Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        else
        {
            Span<byte> temp = stackalloc byte[Size];
            buffer.CopyTo(temp);
            dayNumber = Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(temp));
        }
        
        return CodecHelpers.CreateDateOnly(dayNumber);
    }
}

internal sealed class NullableDateOnlyCodec : IRpcCodec<DateOnly?>
{
    internal static readonly NullableDateOnlyCodec Instance = new();
    private const int Size = 5; // 1 Tag + 4 Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateOnly? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), value.GetValueOrDefault().DayNumber);
        }
        else
        {
            start = 0;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateOnly? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(start)) return null;
            
            var dayNumber = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref start, 1));
            return CodecHelpers.CreateDateOnly(dayNumber);
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (!CodecHelpers.ReadNullablePresence(tempStart)) return null;
        
        var tempDayNumber = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref tempStart, 1));
        return CodecHelpers.CreateDateOnly(tempDayNumber);
    }
}
