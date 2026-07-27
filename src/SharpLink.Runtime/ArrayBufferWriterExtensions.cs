namespace SharpLink.Runtime;

internal static class RpcBufferWriterExtensions
{

    extension(IRpcByteBufferWriter writer)
    {
        private PacketToken WriteHeaderCore(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, ulong requestId)
        {
            var startOffset = writer.WrittenCount;

            var span = writer.GetSpan(ProtocolV2Constants.HeaderBytes);
            span.Clear();
            span[0] = ProtocolV2Constants.Magic;
            span[5] = (byte)frameType;
            span[6] = (byte)flags;
            BinaryPrimitives.WriteUInt64LittleEndian(span[7..15], requestId);

            writer.Advance(ProtocolV2Constants.HeaderBytes);
            return new PacketToken(startOffset);
        }

        public void WritePacket(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, ulong requestId)
        {
            var span = writer.GetSpan(ProtocolV2Constants.HeaderBytes);
            span.Clear();
            span[0] = ProtocolV2Constants.Magic;
            span[5] = (byte)frameType;
            span[6] = (byte)flags;
            BinaryPrimitives.WriteUInt64LittleEndian(span[7..15], requestId);
            writer.Advance(ProtocolV2Constants.HeaderBytes);
        }

        public PacketScope BeginPacketScope(ProtocolV2FrameType frameType, ProtocolV2FrameFlags frameFlags, ulong requestId)
        {
            var token = writer.WriteHeaderCore(frameType, frameFlags, requestId);
            return new PacketScope(writer, token);
        }

        public PacketToken BeginPacket(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, ulong requestId)
            => writer.WriteHeaderCore(type, flags, requestId);

        public void EndPacket(PacketToken token)
        {
            var bodyLength = writer.WrittenCount - token.StartOffset - ProtocolV2Constants.HeaderBytes;
            var span = writer.WrittenSpan;
            var lengthSlice = span.Slice(token.StartOffset + 1, sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(lengthSlice, bodyLength);
        }
    }
}
