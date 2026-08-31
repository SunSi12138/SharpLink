using System.Text;

namespace SharpLink.Runtime;

internal sealed class StringCodec : IRpcCodec<string?>
{
    private const uint NullLength = uint.MaxValue;
    private static readonly UTF8Encoding StrictEncoding = new(false, true);

    internal static readonly StringCodec Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in string? value, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            var nullHeader = writer.GetSpan(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(nullHeader, NullLength);
            writer.Advance(sizeof(uint));
            return;
        }

        var byteCount = StrictEncoding.GetByteCount(value);
        CodecHelpers.EnsureSerializablePayloadLength(byteCount, nameof(value));

        var span = writer.GetSpan(checked(sizeof(uint) + byteCount));
        BinaryPrimitives.WriteUInt32LittleEndian(span, checked((uint)byteCount));
        if (byteCount != 0)
            _ = StrictEncoding.GetBytes(value, span[sizeof(uint)..]);
        writer.Advance(checked(sizeof(uint) + byteCount));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, sizeof(uint));

        uint byteCount;
        if (buffer.FirstSpan.Length >= sizeof(uint))
        {
            byteCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> header = stackalloc byte[sizeof(uint)];
            buffer.Slice(0, sizeof(uint)).CopyTo(header);
            byteCount = BinaryPrimitives.ReadUInt32LittleEndian(header);
        }

        if (byteCount == NullLength)
        {
            CodecHelpers.EnsureExactSize(buffer, sizeof(uint));
            return null;
        }
        if (byteCount > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(uint))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "String payload exceeds the protocol maximum.");

        var payloadLength = checked((int)byteCount);
        CodecHelpers.EnsureExactSize(buffer, (long)sizeof(uint) + payloadLength);
        if (payloadLength == 0)
            return string.Empty;

        var payload = buffer.Slice(sizeof(uint), payloadLength);
        try
        {
            if (payload.FirstSpan.Length >= payloadLength)
                return StrictEncoding.GetString(payload.FirstSpan[..payloadLength]);

            var rented = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                var bytes = rented.AsSpan(0, payloadLength);
                payload.CopyTo(bytes);
                return StrictEncoding.GetString(bytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                "String payload is not valid UTF-8.",
                exception);
        }
    }
}
