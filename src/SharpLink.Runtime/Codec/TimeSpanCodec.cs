namespace SharpLink.Runtime;

internal sealed class TimeSpanCodec : IRpcCodec<TimeSpan>
{
    internal static readonly TimeSpanCodec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in TimeSpan value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            return Unsafe.ReadUnaligned<TimeSpan>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<TimeSpan>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableTimeSpanCodec : IRpcCodec<TimeSpan?>
{
    internal static readonly NullableTimeSpanCodec Instance = new();
    private const int Size = 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in TimeSpan? value, in ArrayBufferWriter<byte> writer)
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
    public TimeSpan? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<TimeSpan>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (tempStart == 0) return null;
        return Unsafe.ReadUnaligned<TimeSpan>(ref Unsafe.Add(ref tempStart, 1));
    }
}