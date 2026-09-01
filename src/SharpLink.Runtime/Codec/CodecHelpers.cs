using System.Text;

namespace SharpLink.Runtime;

internal static class CodecHelpers
{
    private const int Size = 4;
    private const int MaxStackBufferBytes = 1024;
    private const int DateTimeOffsetCollectionElementSize = 16;

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
    public static bool ReadNullablePresence(ref byte marker, int valueBytes)
    {
        if (marker == 1)
            return true;
        if (marker != 0)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"Nullable Codec presence marker {marker} is invalid.");
        }
        ref var value = ref Unsafe.Add(ref marker, 1);
        var hasNonZeroValue = valueBytes switch
        {
            1 => value != 0,
            2 => Unsafe.ReadUnaligned<ushort>(ref value) != 0,
            4 => Unsafe.ReadUnaligned<uint>(ref value) != 0,
            8 => Unsafe.ReadUnaligned<ulong>(ref value) != 0,
            16 => (Unsafe.ReadUnaligned<ulong>(ref value) |
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, sizeof(ulong)))) != 0,
            _ => MemoryMarshal.CreateReadOnlySpan(ref value, valueBytes).IndexOfAnyExcept((byte)0) >= 0
        };
        if (hasNonZeroValue)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                "Nullable Codec null payload contains non-canonical value bytes.");
        }
        return false;
    }

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

    private static DateTimeOffset CreateDateTimeOffsetFromUtcTicks(long utcTicks, short offsetMinutes)
    {
        if ((ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes is < -840 or > 840)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains invalid UTC ticks or offset.");

        var offsetTicks = (long)offsetMinutes * TimeSpan.TicksPerMinute;
        if (offsetTicks > 0 && utcTicks > DateTime.MaxValue.Ticks - offsetTicks ||
            offsetTicks < 0 && utcTicks < -offsetTicks)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains a value outside the supported clock range.");
        }

        return CreateDateTimeOffset(utcTicks + offsetTicks, offsetMinutes);
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDateTimeOffsetBlitPayload(
        ReadOnlySpan<DateTimeOffset> values,
        IBufferWriter<byte> writer)
    {
        if (values.IsEmpty)
            return;

        var payloadBytes = checked(values.Length * DateTimeOffsetCollectionElementSize);
        EnsureSerializablePayloadLength(payloadBytes, nameof(values));
        var destination = writer.GetSpan(payloadBytes)[..payloadBytes];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var element = destination.Slice(index * DateTimeOffsetCollectionElementSize, DateTimeOffsetCollectionElementSize);
            BinaryPrimitives.WriteInt16LittleEndian(element, checked((short)value.Offset.TotalMinutes));
            element.Slice(sizeof(short), 6).Clear();
            BinaryPrimitives.WriteInt64LittleEndian(element.Slice(sizeof(long)), value.UtcTicks);
        }
        writer.Advance(payloadBytes);
    }

    public static DateTimeOffset[]? ReadDateTimeOffsetCollection(in ReadOnlySequence<byte> buffer)
    {
        var length = ReadInt32(buffer);
        if (length < -1)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, $"Invalid collection length {length}.");
        if (length <= 0)
        {
            EnsureExactSize(buffer, sizeof(int));
            return length == -1 ? null : [];
        }

        int payloadBytes;
        try
        {
            payloadBytes = checked(length * DateTimeOffsetCollectionElementSize);
        }
        catch (OverflowException ex)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Collection byte length overflowed.", ex);
        }
        if (payloadBytes > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(int))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Collection payload exceeds the protocol maximum.");
        EnsureExactSize(buffer, (long)sizeof(int) + payloadBytes);

        var result = new DateTimeOffset[length];
        var payload = buffer.Slice(sizeof(int));
        Span<byte> temporary = stackalloc byte[DateTimeOffsetCollectionElementSize];
        for (var index = 0; index < length; index++)
        {
            var encoded = payload.Slice((long)index * DateTimeOffsetCollectionElementSize, DateTimeOffsetCollectionElementSize);
            ReadOnlySpan<byte> element;
            if (encoded.FirstSpan.Length >= DateTimeOffsetCollectionElementSize)
            {
                element = encoded.FirstSpan[..DateTimeOffsetCollectionElementSize];
            }
            else
            {
                encoded.CopyTo(temporary);
                element = temporary;
            }

            if (element.Slice(sizeof(short), 6).IndexOfAnyExcept((byte)0) >= 0)
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains non-canonical padding.");

            var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(element);
            var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(element.Slice(sizeof(long)));
            result[index] = CreateDateTimeOffsetFromUtcTicks(utcTicks, offsetMinutes);
        }
        return result;
    }

    public static DateTimeOffset[] ReadRequiredDateTimeOffsetCollection(in ReadOnlySequence<byte> buffer)
        => ReadDateTimeOffsetCollection(buffer) ?? throw new SharpLinkException(
            SharpLinkErrorCode.DataLoss,
            "A non-nullable memory payload used the reserved null collection marker.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32(IBufferWriter<byte> writer, in int value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(Size);
    }
}
