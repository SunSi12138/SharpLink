namespace SharpLink.Runtime;

internal static class RpcBufferWriterExtensions
{

    extension(IRpcByteBufferWriter writer)
    {
        private PacketToken WriteHeaderCore(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, ulong requestId)
        {
            var token = ProtocolV2FrameWriter.BeginFrame(writer, frameType, flags, requestId);
            return new PacketToken(token.StartOffset);
        }

        public void WritePacket(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, ulong requestId)
            => ProtocolV2FrameWriter.WriteEmptyFrame(writer, frameType, flags, requestId);

        public PacketScope BeginPacketScope(ProtocolV2FrameType frameType, ProtocolV2FrameFlags frameFlags, ulong requestId)
        {
            var token = writer.WriteHeaderCore(frameType, frameFlags, requestId);
            return new PacketScope(writer, token);
        }

        public PacketToken BeginPacket(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, ulong requestId)
            => writer.WriteHeaderCore(type, flags, requestId);

        public void EndPacket(PacketToken token)
            => ProtocolV2FrameWriter.EndFrame(writer, new ProtocolV2FrameToken(token.StartOffset));

    }
}
