namespace SharpLink.Runtime;

internal sealed class DateTimeCodec : IRpcCodec<DateTime>
{
    internal static readonly DateTimeCodec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateTime value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(
            ref MemoryMarshal.GetReference(writer.GetSpan(Size)), 
            value.ToBinary()
        );
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTime Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        long binaryData;

        if (buffer.FirstSpan.Length >= Size)
        {
            binaryData = Unsafe.ReadUnaligned<long>(
                ref MemoryMarshal.GetReference(buffer.FirstSpan)
            );
        }
        else
        {
            Span<byte> temp = stackalloc byte[Size];
            buffer.CopyTo(temp);
            binaryData = Unsafe.ReadUnaligned<long>(
                ref MemoryMarshal.GetReference(temp)
            );
        }

        return CodecHelpers.CreateDateTime(binaryData);
    }
}


internal sealed class NullableDateTimeCodec : IRpcCodec<DateTime?>
{
    internal static readonly NullableDateTimeCodec Instance = new();
    private const int Size = 9; // 1 byte Tag + 8 bytes Value (long)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateTime? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1; // 写入 Tag
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref start, 1), 
                value.GetValueOrDefault().ToBinary()
            );
        }
        else
        {
            start = 0; // Tag = 0
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref start, 1), 
                0L
            );
        }
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTime? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            
            if (!CodecHelpers.ReadNullablePresence(start)) return null;
            
            var data = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref start, 1));
            return CodecHelpers.CreateDateTime(data);
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (!CodecHelpers.ReadNullablePresence(tempStart)) return null;
        
        var stackData = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref tempStart, 1));
        return CodecHelpers.CreateDateTime(stackData);
    }
}
