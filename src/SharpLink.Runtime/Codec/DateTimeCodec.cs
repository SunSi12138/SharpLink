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
            value
        );
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTime Deserialize(in ReadOnlySequence<byte> buffer)
        => ValidateRaw(CodecHelpers.ReadUnmanaged<DateTime>(buffer));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DateTime ValidateRaw(DateTime value)
    {
        if ((ulong)value.Ticks > (ulong)DateTime.MaxValue.Ticks)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateTime payload.");
        return value;
    }
}


internal sealed class NullableDateTimeCodec : IRpcCodec<DateTime?>
{
    internal static readonly NullableDateTimeCodec Instance = new();
    private const int Size = 9; // 1 byte Tag + 8 bytes DateTime

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in DateTime? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1; // 写入 Tag
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref start, 1),
                value.GetValueOrDefault()
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

            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;

            var value = Unsafe.ReadUnaligned<DateTime>(ref Unsafe.Add(ref start, 1));
            return DateTimeCodec.ValidateRaw(value);
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);

        if (!CodecHelpers.ReadNullablePresence(ref tempStart, Size - 1)) return null;

        var stackValue = Unsafe.ReadUnaligned<DateTime>(ref Unsafe.Add(ref tempStart, 1));
        return DateTimeCodec.ValidateRaw(stackValue);
    }
}
