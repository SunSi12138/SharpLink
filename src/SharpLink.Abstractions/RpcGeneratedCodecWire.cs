using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpLink.Abstractions;

/// <summary>Identifies the bounded payload representation of one generated DTO field.</summary>
public enum RpcGeneratedWireType : byte
{
    /// <summary>A null value with no payload.</summary>
    Null = 0,
    /// <summary>A one-byte fixed value.</summary>
    Fixed1 = 1,
    /// <summary>A two-byte fixed value.</summary>
    Fixed2 = 2,
    /// <summary>A four-byte fixed value.</summary>
    Fixed4 = 3,
    /// <summary>An eight-byte fixed value.</summary>
    Fixed8 = 4,
    /// <summary>A sixteen-byte fixed value.</summary>
    Fixed16 = 5,
    /// <summary>A UInt32-length-prefixed value.</summary>
    LengthDelimited = 6
}

/// <summary>Identifies a generated length prefix awaiting backfill.</summary>
public readonly record struct RpcGeneratedLengthToken(int Offset);

/// <summary>Provides allocation-free primitives used only by source-generated Codecs.</summary>
public static class RpcGeneratedCodecWire
{
    /// <summary>The hard maximum number of items allocated by one generated collection Codec.</summary>
    public const int MaximumCollectionItems = 1_048_576;

    /// <summary>The largest UTF-16 payload, in bytes, accepted by the generated string Codec.</summary>
    public const int MaximumStringPayloadBytes = 64 * 1024 * 1024 - sizeof(int);

    /// <summary>Writes one DTO field key.</summary>
    public static void WriteFieldKey(IBufferWriter<byte> writer, uint fieldId, RpcGeneratedWireType wireType)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (fieldId is 0 or > 0x1FFF_FFFFU)
            throw new ArgumentOutOfRangeException(nameof(fieldId));
        if (wireType > RpcGeneratedWireType.LengthDelimited)
            throw new ArgumentOutOfRangeException(nameof(wireType));
        WriteVarUInt32(writer, (fieldId << 3) | (byte)wireType);
    }

    /// <summary>Writes the object terminator field key.</summary>
    public static void WriteObjectEnd(IBufferWriter<byte> writer) => WriteVarUInt32(writer, 0);

    /// <summary>Reads one DTO field key, returning false only for an explicit terminator.</summary>
    public static bool TryReadField(
        ref SequenceReader<byte> reader,
        out uint fieldId,
        out RpcGeneratedWireType wireType)
    {
        var key = ReadVarUInt32(ref reader, "DTO field key");
        if (key == 0)
        {
            fieldId = 0;
            wireType = default;
            return false;
        }

        fieldId = key >> 3;
        var rawWireType = (byte)(key & 7);
        if (fieldId == 0 || rawWireType > (byte)RpcGeneratedWireType.LengthDelimited)
            throw DataLoss("DTO field key contains an invalid field ID or wire type.");
        wireType = (RpcGeneratedWireType)rawWireType;
        return true;
    }

    /// <summary>Writes a bounded unmanaged scalar.</summary>
    public static void WriteUnmanaged<T>(IBufferWriter<byte> writer, in T value) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(writer);
        var size = Unsafe.SizeOf<T>();
        ValidateFixedSize(size);
        var span = writer.GetSpan(size);
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
        writer.Advance(size);
    }

    /// <summary>Reads a bounded unmanaged scalar across single- or multi-segment input.</summary>
    public static T ReadUnmanaged<T>(ref SequenceReader<byte> reader) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        ValidateFixedSize(size);
        if (reader.Remaining < size)
            throw DataLoss("Generated scalar payload is truncated.");

        T value;
        if (reader.UnreadSpan.Length >= size)
        {
            value = Unsafe.ReadUnaligned<T>(in MemoryMarshal.GetReference(reader.UnreadSpan));
        }
        else
        {
            Span<byte> temporary = stackalloc byte[16];
            var target = temporary[..size];
            if (!reader.TryCopyTo(target))
                throw DataLoss("Generated scalar payload is truncated.");
            value = Unsafe.ReadUnaligned<T>(in MemoryMarshal.GetReference(target));
        }
        reader.Advance(size);
        return value;
    }

    /// <summary>Writes one canonical Boolean marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBoolean(IBufferWriter<byte> writer, bool value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(1);
        span[0] = value ? (byte)1 : (byte)0;
        writer.Advance(1);
    }

    /// <summary>Reads one canonical Boolean marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolean(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var marker) || marker is not (0 or 1))
            throw DataLoss("Generated Boolean payload is missing or non-canonical.");
        return marker == 1;
    }

    /// <summary>Writes one Rune using its fixed native representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRune(IBufferWriter<byte> writer, Rune value) => WriteUnmanaged(writer, value);

    /// <summary>Reads and validates one Rune.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rune ReadRune(ref SequenceReader<byte> reader)
    {
        var value = ReadUnmanaged<Rune>(ref reader);
        if (!Rune.IsValid(value.Value))
            throw DataLoss("Generated Rune payload is not a valid Unicode scalar.");
        return value;
    }

    /// <summary>Writes one decimal using its fixed native representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDecimal(IBufferWriter<byte> writer, decimal value) => WriteUnmanaged(writer, value);

    /// <summary>Reads and validates one decimal.</summary>
    public static decimal ReadDecimal(ref SequenceReader<byte> reader)
    {
        var value = ReadUnmanaged<decimal>(ref reader);
        try
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            return new decimal(bits);
        }
        catch (ArgumentException exception)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Generated decimal payload is invalid.", exception);
        }
    }

    /// <summary>Writes one DateOnly using its fixed native representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDateOnly(IBufferWriter<byte> writer, DateOnly value) => WriteUnmanaged(writer, value);

    /// <summary>Reads and validates one DateOnly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateOnly ReadDateOnly(ref SequenceReader<byte> reader)
    {
        var value = ReadUnmanaged<DateOnly>(ref reader);
        if ((uint)value.DayNumber > (uint)DateOnly.MaxValue.DayNumber)
            throw DataLoss("Generated DateOnly payload is outside the supported calendar range.");
        return value;
    }

    /// <summary>Writes one DateTime using its fixed native representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDateTime(IBufferWriter<byte> writer, DateTime value) => WriteUnmanaged(writer, value);

    /// <summary>Reads and validates one DateTime.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime ReadDateTime(ref SequenceReader<byte> reader)
    {
        var value = ReadUnmanaged<DateTime>(ref reader);
        if ((ulong)value.Ticks > (ulong)DateTime.MaxValue.Ticks)
            throw DataLoss("Generated DateTime payload is outside the supported calendar range.");
        return value;
    }

    /// <summary>Writes one TimeOnly using its fixed native representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTimeOnly(IBufferWriter<byte> writer, TimeOnly value) => WriteUnmanaged(writer, value);

    /// <summary>Reads and validates one TimeOnly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TimeOnly ReadTimeOnly(ref SequenceReader<byte> reader)
    {
        var value = ReadUnmanaged<TimeOnly>(ref reader);
        if ((ulong)value.Ticks >= TimeSpan.TicksPerDay)
            throw DataLoss("Generated TimeOnly payload is outside one day.");
        return value;
    }

    /// <summary>Writes the canonical 16-byte generated DTO representation of one DateTimeOffset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDateTimeOffset(IBufferWriter<byte> writer, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        const int size = 16;
        var span = writer.GetSpan(size);
        BinaryPrimitives.WriteInt16LittleEndian(span, checked((short)value.Offset.TotalMinutes));
        span[sizeof(short)..sizeof(long)].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(span[sizeof(long)..], value.UtcDateTime.Ticks);
        writer.Advance(size);
    }

    /// <summary>Reads and validates the canonical 16-byte generated DTO DateTimeOffset representation.</summary>
    public static DateTimeOffset ReadDateTimeOffset(ref SequenceReader<byte> reader)
    {
        const int size = 16;
        if (reader.Remaining < size)
            throw DataLoss("Generated DateTimeOffset payload is truncated.");

        Span<byte> temporary = stackalloc byte[size];
        ReadOnlySpan<byte> payload;
        if (reader.UnreadSpan.Length >= size)
        {
            payload = reader.UnreadSpan[..size];
        }
        else
        {
            if (!reader.TryCopyTo(temporary))
                throw DataLoss("Generated DateTimeOffset payload is truncated.");
            payload = temporary;
        }

        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload);
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(long)..]);
        if ((ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes is < -840 or > 840)
            throw DataLoss("Generated DateTimeOffset payload contains invalid UTC ticks or offset.");
        if (!payload[sizeof(short)..sizeof(long)].IsEmpty &&
            payload[sizeof(short)..sizeof(long)].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw DataLoss("Generated DateTimeOffset payload contains non-canonical padding.");
        }
        var offsetTicks = (long)offsetMinutes * TimeSpan.TicksPerMinute;
        if (offsetTicks > 0 && utcTicks > DateTime.MaxValue.Ticks - offsetTicks ||
            offsetTicks < 0 && utcTicks < -offsetTicks)
        {
            throw DataLoss("Generated DateTimeOffset payload is outside the supported clock range.");
        }
        reader.Advance(size);
        return new DateTimeOffset(utcTicks + offsetTicks, TimeSpan.FromMinutes(offsetMinutes));
    }

    /// <summary>Returns the fixed wire type for a supported unmanaged size.</summary>
    public static RpcGeneratedWireType GetFixedWireType(int size) => size switch
    {
        1 => RpcGeneratedWireType.Fixed1,
        2 => RpcGeneratedWireType.Fixed2,
        4 => RpcGeneratedWireType.Fixed4,
        8 => RpcGeneratedWireType.Fixed8,
        16 => RpcGeneratedWireType.Fixed16,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    /// <summary>Validates the actual wire type for a known field.</summary>
    public static void EnsureWireType(RpcGeneratedWireType actual, RpcGeneratedWireType expected)
    {
        if (actual != expected)
            throw DataLoss($"Generated field expected wire type {expected}, but received {actual}.");
    }

    /// <summary>Writes a one-byte object presence marker.</summary>
    public static void WritePresence(IBufferWriter<byte> writer, bool present)
    {
        var span = writer.GetSpan(1);
        span[0] = present ? (byte)1 : (byte)0;
        writer.Advance(1);
    }

    /// <summary>Reads and validates a one-byte object presence marker.</summary>
    public static bool ReadPresence(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var marker) || marker > 1)
            throw DataLoss("Generated object presence marker is missing or invalid.");
        return marker != 0;
    }

    /// <summary>Writes a UTF-16LE string payload including its signed Int32 byte length.</summary>
    public static void WriteString(IBufferWriter<byte> writer, string value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = checked(value.Length * sizeof(char));
        if (byteCount > MaximumStringPayloadBytes)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Generated string payload exceeds the protocol maximum.");
        WriteInt32(writer, byteCount);
        if (byteCount == 0)
            return;
        var span = writer.GetSpan(byteCount);
        value.AsSpan().CopyTo(MemoryMarshal.Cast<byte, char>(span));
        writer.Advance(byteCount);
    }

    /// <summary>Reads a bounded UTF-16LE string payload.</summary>
    public static string ReadString(ref SequenceReader<byte> reader)
    {
        var byteCount = ReadInt32(ref reader);
        if (byteCount < 0 || (byteCount & 1) != 0 || byteCount > MaximumStringPayloadBytes || reader.Remaining < byteCount)
            throw DataLoss("Generated UTF-16 string byte length is invalid, truncated, or too large.");
        if (byteCount == 0)
            return string.Empty;

        var payload = reader.Sequence.Slice(reader.Position, byteCount);
        reader.Advance(byteCount);
        if (payload.FirstSpan.Length >= byteCount)
            return new string(MemoryMarshal.Cast<byte, char>(payload.FirstSpan[..byteCount]));
        return string.Create(byteCount / sizeof(char), payload, static (destination, sequence) =>
        {
            sequence.CopyTo(MemoryMarshal.AsBytes(destination));
        });
    }

    /// <summary>Reserves a UInt32 length prefix in a contiguous SharpLink packet writer.</summary>
    public static RpcGeneratedLengthToken BeginLength(IRpcByteBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var offset = writer.WrittenCount;
        var span = writer.GetSpan(sizeof(uint));
        span[..sizeof(uint)].Clear();
        writer.Advance(sizeof(uint));
        return new RpcGeneratedLengthToken(offset);
    }

    /// <summary>Backfills a previously reserved UInt32 length prefix.</summary>
    public static void EndLength(IRpcByteBufferWriter writer, RpcGeneratedLengthToken token)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var length = writer.WrittenCount - token.Offset - sizeof(uint);
        if (length < 0)
            throw new ArgumentException("Length token does not belong to this writer.", nameof(token));
        BinaryPrimitives.WriteUInt32LittleEndian(
            writer.WrittenSpan.Slice(token.Offset, sizeof(uint)),
            checked((uint)length));
    }

    /// <summary>Reads and consumes one UInt32-length-prefixed sequence.</summary>
    public static ReadOnlySequence<byte> ReadLengthDelimited(ref SequenceReader<byte> reader)
    {
        var length = ReadUInt32(ref reader);
        if (length > int.MaxValue || reader.Remaining < length)
            throw DataLoss("Generated length-delimited payload is truncated or too large.");
        var payload = reader.Sequence.Slice(reader.Position, length);
        reader.Advance(length);
        return payload;
    }

    /// <summary>Skips one unknown field without allocating.</summary>
    public static void SkipField(ref SequenceReader<byte> reader, RpcGeneratedWireType wireType)
    {
        var fixedBytes = wireType switch
        {
            RpcGeneratedWireType.Null => 0,
            RpcGeneratedWireType.Fixed1 => 1,
            RpcGeneratedWireType.Fixed2 => 2,
            RpcGeneratedWireType.Fixed4 => 4,
            RpcGeneratedWireType.Fixed8 => 8,
            RpcGeneratedWireType.Fixed16 => 16,
            RpcGeneratedWireType.LengthDelimited => -1,
            _ => throw DataLoss("Unknown generated field wire type.")
        };
        if (fixedBytes < 0)
        {
            _ = ReadLengthDelimited(ref reader);
            return;
        }
        if (reader.Remaining < fixedBytes)
            throw DataLoss("Unknown generated field payload is truncated.");
        reader.Advance(fixedBytes);
    }

    /// <summary>Writes a null marker or a non-null collection count.</summary>
    public static void WriteCollectionCount(IBufferWriter<byte> writer, int count, bool isNull)
    {
        if (isNull)
        {
            WriteVarUInt32(writer, 0);
            return;
        }
        if ((uint)count > MaximumCollectionItems)
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Generated collection contains more than {MaximumCollectionItems} items.");
        WriteVarUInt32(writer, checked((uint)count + 1));
    }

    /// <summary>Reads a collection count; -1 represents null.</summary>
    public static int ReadCollectionCount(ref SequenceReader<byte> reader)
    {
        var marker = ReadVarUInt32(ref reader, "collection count");
        if (marker == 0)
            return -1;
        var count = marker - 1;
        if (count > MaximumCollectionItems || (count != 0 && count > reader.Remaining / sizeof(uint)))
            throw DataLoss("Generated collection count exceeds its bounded payload.");
        return checked((int)count);
    }

    /// <summary>Ensures a generated root payload has no trailing bytes.</summary>
    public static void EnsureFullyConsumed(in SequenceReader<byte> reader)
    {
        if (reader.Remaining != 0)
            throw DataLoss("Generated Codec payload contains trailing bytes.");
    }

    /// <summary>Creates the structured error used for invalid generated payloads.</summary>
    public static SharpLinkException DataLoss(string message)
        => new(SharpLinkErrorCode.DataLoss, message);

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(sizeof(int));
    }

    private static int ReadInt32(ref SequenceReader<byte> reader)
    {
        if (reader.UnreadSpan.Length >= sizeof(int))
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan);
            reader.Advance(sizeof(int));
            return value;
        }
        Span<byte> temporary = stackalloc byte[sizeof(int)];
        if (!reader.TryCopyTo(temporary))
            throw DataLoss("Generated Int32 length is truncated.");
        reader.Advance(sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(temporary);
    }

    private static void WriteUInt32(IBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(sizeof(uint));
    }

    private static uint ReadUInt32(ref SequenceReader<byte> reader)
    {
        if (reader.UnreadSpan.Length >= sizeof(uint))
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(reader.UnreadSpan);
            reader.Advance(sizeof(uint));
            return value;
        }
        Span<byte> temporary = stackalloc byte[sizeof(uint)];
        if (!reader.TryCopyTo(temporary))
            throw DataLoss("Generated UInt32 length is truncated.");
        reader.Advance(sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(temporary);
    }

    private static void WriteVarUInt32(IBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(5);
        var written = 0;
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                current |= 0x80;
            span[written++] = current;
        } while (value != 0);
        writer.Advance(written);
    }

    private static uint ReadVarUInt32(ref SequenceReader<byte> reader, string fieldName)
    {
        uint value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (!reader.TryRead(out var current))
                throw DataLoss($"Generated {fieldName} is truncated.");
            if (index == 4 && (current & 0xF0) != 0)
                throw DataLoss($"Generated {fieldName} overflows UInt32.");
            value |= (uint)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
                return value;
        }
        throw DataLoss($"Generated {fieldName} is invalid.");
    }

    private static void ValidateFixedSize(int size)
    {
        if (size is not (1 or 2 or 4 or 8 or 16))
            throw new ArgumentOutOfRangeException(nameof(size), "Generated fixed values must be 1, 2, 4, 8, or 16 bytes.");
    }
}
