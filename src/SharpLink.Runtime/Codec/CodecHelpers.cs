using System.Text;

namespace SharpLink.Runtime;

internal static class CodecHelpers
{
    private const int Size = 4;
    private const int MaxStackBufferBytes = 1024;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureAvailable(in ReadOnlySequence<byte> buffer, long requiredBytes)
    {
        if (requiredBytes < 0 || buffer.Length < requiredBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"Codec input is truncated: required {requiredBytes} bytes, received {buffer.Length} bytes.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureExactSize(in ReadOnlySequence<byte> buffer, long requiredBytes)
    {
        if (requiredBytes < 0 || buffer.Length != requiredBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"Codec input length is invalid: required exactly {requiredBytes} bytes, received {buffer.Length} bytes.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadNullablePresence(byte marker)
        => marker switch
        {
            0 => false,
            1 => true,
            _ => throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"Nullable Codec presence marker {marker} is invalid.")
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(in ReadOnlySequence<byte> buffer)
    {
        EnsureAvailable(buffer, sizeof(int));
        if (buffer.FirstSpan.Length >= sizeof(int))
            return BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);

        Span<byte> header = stackalloc byte[sizeof(int)];
        buffer.Slice(0, sizeof(int)).CopyTo(header);
        return BinaryPrimitives.ReadInt32LittleEndian(header);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReadUnmanaged<T>(in ReadOnlySequence<byte> buffer)
    {
        var size = Unsafe.SizeOf<T>();
        if (size > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, $"Codec value size {size} exceeds the protocol maximum.");

        EnsureExactSize(buffer, size);
        if (buffer.FirstSpan.Length >= size)
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buffer.FirstSpan));

        if (size <= MaxStackBufferBytes)
        {
            Span<byte> temporary = stackalloc byte[size];
            buffer.Slice(0, size).CopyTo(temporary);
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(temporary));
        }

        var rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var temporary = rented.AsSpan(0, size);
            buffer.Slice(0, size).CopyTo(temporary);
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(temporary));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static int GetValidatedCollectionByteCount<T>(
        in ReadOnlySequence<byte> buffer,
        int length)
        where T : unmanaged
    {
        if (length < -1)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, $"Invalid collection length {length}.");
        if (length <= 0)
        {
            EnsureExactSize(buffer, sizeof(int));
            return 0;
        }

        int byteCount;
        try
        {
            byteCount = checked(length * Unsafe.SizeOf<T>());
        }
        catch (OverflowException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Collection byte length overflowed.", ex);
        }

        if (byteCount > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(int))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Collection payload exceeds the protocol maximum.");

        EnsureExactSize(buffer, (long)sizeof(int) + byteCount);
        return byteCount;
    }

    public static void EnsureSerializablePayloadLength(int payloadBytes, string parameterName)
    {
        if (payloadBytes > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(int))
            throw new ArgumentOutOfRangeException(parameterName, "Serialized payload exceeds the protocol maximum.");
    }

    public static DateOnly CreateDateOnly(int dayNumber)
    {
        try
        {
            return DateOnly.FromDayNumber(dayNumber);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateOnly payload.", ex);
        }
    }

    public static DateTime CreateDateTime(long binaryData)
    {
        try
        {
            return DateTime.FromBinary(binaryData);
        }
        catch (ArgumentException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateTime payload.", ex);
        }
    }

    public static DateTimeOffset CreateDateTimeOffset(long ticks, short offsetMinutes)
    {
        try
        {
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
        }
        catch (ArgumentException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateTimeOffset payload.", ex);
        }
    }

    public static TimeOnly ValidateTimeOnly(TimeOnly value)
    {
        if ((ulong)value.Ticks >= TimeSpan.TicksPerDay)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid TimeOnly payload.");
        return value;
    }

    public static Rune ValidateRune(Rune value)
    {
        if (!Rune.IsValid(value.Value))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid Rune payload.");
        return value;
    }

    public static decimal ValidateDecimal(decimal value)
    {
        try
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            return new decimal(bits);
        }
        catch (ArgumentException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid Decimal payload.", ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ValidateBlitElements<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        if (typeof(T) == typeof(bool))
        {
            var bytes = MemoryMarshal.AsBytes(values);
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] > 1)
                    throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Boolean collection contains a non-canonical element.");
            return;
        }
        if (typeof(T) == typeof(Rune))
        {
            var typed = MemoryMarshal.Cast<T, Rune>(values);
            for (var index = 0; index < typed.Length; index++)
                _ = ValidateRune(typed[index]);
            return;
        }
        if (typeof(T) == typeof(decimal))
        {
            var typed = MemoryMarshal.Cast<T, decimal>(values);
            for (var index = 0; index < typed.Length; index++)
                _ = ValidateDecimal(typed[index]);
            return;
        }
        if (typeof(T) == typeof(DateOnly))
        {
            var typed = MemoryMarshal.Cast<T, DateOnly>(values);
            for (var index = 0; index < typed.Length; index++)
                _ = CreateDateOnly(typed[index].DayNumber);
            return;
        }
        if (typeof(T) == typeof(DateTime))
        {
            var typed = MemoryMarshal.Cast<T, DateTime>(values);
            for (var index = 0; index < typed.Length; index++)
            {
                var value = typed[index];
                _ = CreateDateTime(Unsafe.As<DateTime, long>(ref value));
            }
            return;
        }
        if (typeof(T) == typeof(TimeOnly))
        {
            var typed = MemoryMarshal.Cast<T, TimeOnly>(values);
            for (var index = 0; index < typed.Length; index++)
                _ = ValidateTimeOnly(typed[index]);
            return;
        }
        if (typeof(T) == typeof(DateTimeOffset))
            ValidateDateTimeOffsetElements(MemoryMarshal.AsBytes(values));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NormalizeDateTimeOffsetBlitPayload(Span<byte> payload)
    {
        const int size = 16;
        for (var offset = 0; offset < payload.Length; offset += size)
            payload.Slice(offset + sizeof(short), 6).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDateTimeOffsetBlitPayload(
        ReadOnlySpan<DateTimeOffset> values,
        IBufferWriter<byte> writer)
    {
        if (values.IsEmpty)
            return;
        var source = MemoryMarshal.AsBytes(values);
        EnsureSerializablePayloadLength(source.Length, nameof(values));
        var destination = writer.GetSpan(source.Length)[..source.Length];
        source.CopyTo(destination);
        NormalizeDateTimeOffsetBlitPayload(destination);
        writer.Advance(source.Length);
    }

    private static void ValidateDateTimeOffsetElements(ReadOnlySpan<byte> payload)
    {
        const int size = 16;
        for (var offset = 0; offset < payload.Length; offset += size)
        {
            var element = payload[offset..];
            var offsetMinutes = Unsafe.ReadUnaligned<short>(ref MemoryMarshal.GetReference(element));
            var utcTicks = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(
                ref MemoryMarshal.GetReference(element), sizeof(long)));
            if ((ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes is < -840 or > 840)
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains invalid UTC ticks or offset.");
            var offsetTicks = (long)offsetMinutes * TimeSpan.TicksPerMinute;
            if (offsetTicks > 0 && utcTicks > DateTime.MaxValue.Ticks - offsetTicks ||
                offsetTicks < 0 && utcTicks < -offsetTicks)
            {
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains a value outside the supported clock range.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32(IBufferWriter<byte> writer, in int value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(Size);
    }
}
