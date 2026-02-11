namespace SharpLink.Runtime;

public static class PacketHelper
{
    public static bool TryReadMessage(ref ReadOnlySequence<byte> buffer,out PacketHeader header, out ReadOnlySequence<byte> payload)
    {
        payload = default;
        header = default;
        
        if (buffer.Length < ProtocolConstants.HeaderBytes) return false;

        Span<byte> headerBytes = stackalloc byte[ProtocolConstants.HeaderBytes];
        buffer.Slice(0, ProtocolConstants.HeaderBytes).CopyTo(headerBytes);

        if (headerBytes[ProtocolConstants.MagicNumberOffset] != ProtocolConstants.MagicNumber)
            throw new InvalidDataException("Bad Magic");

        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[ProtocolConstants.PacketLengthRange]);

        if (buffer.Length < 15 + packetLength) return false; // 半包，等待更多数据

        var type = (PacketType)headerBytes[ProtocolConstants.PacketTypeOffset];
        var flags = (PacketFlags)headerBytes[ProtocolConstants.PacketFlagsOffset];
        var requestId = BinaryPrimitives.ReadInt64LittleEndian(headerBytes[ProtocolConstants.PacketRequestIdRange]);
        payload = buffer.Slice(ProtocolConstants.HeaderBytes, packetLength);
        header = new PacketHeader(type, flags, requestId);
        buffer = buffer.Slice(ProtocolConstants.HeaderBytes + packetLength);
        
        return true;
    }
}
