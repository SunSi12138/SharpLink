namespace SharpLink.Runtime;

internal sealed class StringCodec : IRpcCodec<string?>
{
    internal static readonly StringCodec Instance = new();
    private const int CharSize = 2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in string? value, IBufferWriter<byte> writer)
    {
        if (value is null)
        {
            CodecHelpers.WriteInt32(writer, -1);
            return;
        }

        if (value.Length == 0)
        {
            CodecHelpers.WriteInt32(writer, 0);
            return;
        }
        
        var bytesCount = checked(value.Length * CharSize);
        CodecHelpers.EnsureSerializablePayloadLength(bytesCount, nameof(value));

        var span = writer.GetSpan(bytesCount + 4);

        BinaryPrimitives.WriteInt32LittleEndian(span[..4], bytesCount);
        
        value.AsSpan().CopyTo(MemoryMarshal.Cast<byte, char>(span[4..]));
        
        writer.Advance(bytesCount + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var bytesCount = CodecHelpers.ReadInt32(buffer);
        if (bytesCount == -1)
            return null;
        if (bytesCount < -1)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, $"Invalid string byte length {bytesCount}.");

        if (bytesCount == 0) return string.Empty;
        if ((bytesCount & 1) != 0)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "UTF-16 string byte length must be even.");
        if (bytesCount > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(int))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "String payload exceeds the protocol maximum.");

        CodecHelpers.EnsureAvailable(buffer, (long)sizeof(int) + bytesCount);
        var payload = buffer.Slice(sizeof(int), bytesCount);

        if (payload.FirstSpan.Length >= bytesCount)
        {
            var charSpan = MemoryMarshal.Cast<byte, char>(payload.FirstSpan[..bytesCount]);
            return new string(charSpan);
        }

        return string.Create(bytesCount / CharSize, payload, static (destination, sequence) =>
        {
            sequence.CopyTo(MemoryMarshal.AsBytes(destination));
        });
    }
}
