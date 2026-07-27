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
    private static readonly UTF8Encoding SStrictUtf8 = new(false, true);

    /// <summary>The hard maximum number of items allocated by one generated collection Codec.</summary>
    public const int MaximumCollectionItems = 1_048_576;

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

    /// <summary>Writes a UTF-8 string payload including its UInt32 byte length.</summary>
    public static void WriteString(IBufferWriter<byte> writer, string value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = SStrictUtf8.GetByteCount(value);
        WriteUInt32(writer, checked((uint)byteCount));
        if (byteCount == 0)
            return;
        var span = writer.GetSpan(byteCount);
        var written = SStrictUtf8.GetBytes(value, span);
        writer.Advance(written);
    }

    /// <summary>Reads a bounded UTF-8 string payload.</summary>
    public static string ReadString(ref SequenceReader<byte> reader)
    {
        var payload = ReadLengthDelimited(ref reader);
        if (payload.IsSingleSegment)
            return DecodeUtf8(payload.FirstSpan);
        return DecodeUtf8(payload.ToArray());
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> payload)
    {
        var value = Encoding.UTF8.GetString(payload);
        if (!value.AsSpan().Contains('\uFFFD'))
            return value;
        try
        {
            _ = SStrictUtf8.GetCharCount(payload);
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                "Generated string payload is not valid UTF-8.",
                exception);
        }
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
