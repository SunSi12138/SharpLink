namespace SharpLink.Runtime;

internal sealed class DateTimeOffsetCodec : IRpcCodec<DateTimeOffset>
{
    internal static readonly DateTimeOffsetCodec Instance = new();
    private const int Size = 10; 

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateTimeOffset value, in ArrayBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));
        
        Unsafe.WriteUnaligned(ref start, value.Ticks);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 8), (short)value.Offset.TotalMinutes);
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTimeOffset Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        long ticks;
        short offsetMinutes;

        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            ticks = Unsafe.ReadUnaligned<long>(ref start);
            offsetMinutes = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref start, 8));
        }
        else
        {
            Span<byte> temp = stackalloc byte[Size];
            buffer.CopyTo(temp);
            
            ref var start = ref MemoryMarshal.GetReference(temp);
            ticks = Unsafe.ReadUnaligned<long>(ref start);
            offsetMinutes = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref start, 8));
        }

        return CodecHelpers.CreateDateTimeOffset(ticks, offsetMinutes);
    }
}

internal sealed class NullableDateTimeOffsetCodec : IRpcCodec<DateTimeOffset?>
{
    internal static readonly NullableDateTimeOffsetCodec Instance = new();
    private const int Size = 11;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateTimeOffset? value, in ArrayBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            var val = value.GetValueOrDefault();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), val.Ticks);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 9), (short)val.Offset.TotalMinutes);
        }
        else
        {
            start = 0;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 1), 0L);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 9), (short)0);
        }
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTimeOffset? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;

            var ticks = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref start, 1));
            var offsetMinutes = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref start, 9));
            
            return CodecHelpers.CreateDateTimeOffset(ticks, offsetMinutes);
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (tempStart == 0) return null;

        var t = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref tempStart, 1));
        var o = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref tempStart, 9));
            
        return CodecHelpers.CreateDateTimeOffset(t, o);
    }
}
