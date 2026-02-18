using System.Text;

namespace SharpLink.Runtime;

public static class ArrayBufferWriterExtensions
{

    extension(ArrayBufferWriter<byte> writer)
    {
        private PacketToken WriteHeaderCore(PacketType packetType, PacketFlags flags, long requestId)
        {
            var startOffset = writer.WrittenCount;

            var span = writer.GetSpan(ProtocolConstants.HeaderBytes);
            span[ProtocolConstants.MagicNumberOffset] = ProtocolConstants.MagicNumber;
            span[ProtocolConstants.PacketTypeOffset] = (byte)packetType;
            span[ProtocolConstants.PacketFlagsOffset] = (byte)flags;
            BinaryPrimitives.WriteInt64LittleEndian(span[ProtocolConstants.PacketRequestIdRange], requestId);

            writer.Advance(ProtocolConstants.HeaderBytes);
            return new PacketToken(startOffset);
        }

        public void WritePacket(PacketType packetType, PacketFlags flags, long requestId)
        {
            var span = writer.GetSpan(ProtocolConstants.HeaderBytes);
            span[ProtocolConstants.MagicNumberOffset] = ProtocolConstants.MagicNumber;
            span[ProtocolConstants.PacketTypeOffset] = (byte)packetType;
            span[ProtocolConstants.PacketFlagsOffset] = (byte)flags;
            BinaryPrimitives.WriteInt64LittleEndian(span[ProtocolConstants.PacketRequestIdRange], requestId);
            writer.Advance(ProtocolConstants.HeaderBytes);
        }

        public PacketScope BeginPacketScope(PacketType packetType, PacketFlags packetFlags, long requestId)
        {
            var token = writer.WriteHeaderCore(packetType, packetFlags, requestId);
            return new PacketScope(writer, token);
        }

        public PacketToken BeginPacket(PacketType type, PacketFlags flags, long requestId) => writer.WriteHeaderCore(type, flags, requestId);

        public void EndPacket(PacketToken token)
        {
            var bodyLength = writer.WrittenCount - token.StartOffset - ProtocolConstants.HeaderBytes;
            var span = MemoryMarshal.AsMemory(writer.WrittenMemory).Span;
            var lengthSlice = span.Slice(token.StartOffset + ProtocolConstants.PacketLengthOffset, 4);
            BinaryPrimitives.WriteInt32LittleEndian(lengthSlice, bodyLength);
        }
    }

    extension(IBufferWriter<byte> writer)
    {
        public void WriteUtf8String(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            var maxLen = Encoding.UTF8.GetMaxByteCount(value.Length);
            var span = writer.GetSpan(maxLen);
            var longWritten = Encoding.UTF8.GetBytes(value, span);
            writer.Advance(longWritten);
        }
    }
}
