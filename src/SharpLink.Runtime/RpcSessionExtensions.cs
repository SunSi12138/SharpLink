namespace SharpLink.Runtime;

public static class RpcSessionExtensions
{
    extension(IRpcSession session)
    {
        public async ValueTask SendStringPacketAsync(PacketType packetType, PacketFlags flags, long requestId, string message)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(packetType, flags, requestId))
            {
                writer.WriteUtf8String(message);
            }

            await session.SendPacketAsync(writer);
        }
        public async ValueTask SendPacketAsync(PacketType packetType, PacketFlags flags, long requestId)
        {
            var writer = BufferWriterPool.Get();
            writer.WritePacket(packetType, flags, requestId);
            await session.SendPacketAsync(writer);
        }

        public ValueTask SendCancelAsync(long requestId)
            => session.SendPacketAsync(PacketType.Cancel, PacketFlags.None, requestId);

        public async ValueTask SendStreamChunkAsync<T>(long requestId, sbyte streamId, T item)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamChunk, PacketFlags.None, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
                session.Serializer.Serialize(item, writer);
            }

            await session.SendPacketAsync(writer);
        }

        public async ValueTask SendStreamCompleteAsync(long requestId, sbyte streamId)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamComplete, PacketFlags.None, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
            }

            await session.SendPacketAsync(writer);
        }

        public async ValueTask SendStreamErrorAsync(long requestId, sbyte streamId, string errorMessage)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamError, PacketFlags.IsError, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
                writer.WriteUtf8String(errorMessage);
            }

            await session.SendPacketAsync(writer);
        }
    }
}
