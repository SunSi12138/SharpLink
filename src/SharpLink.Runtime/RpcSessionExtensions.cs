namespace SharpLink.Runtime;

public static class RpcSessionExtensions
{
    extension(IRpcSession session)
    {
        public void SendStringPacketAsync(PacketType packetType, PacketFlags flags, long requestId, string message)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(packetType, flags, requestId))
            {
                writer.WriteUtf8String(message);
            }

            session.SendPacket(writer);
        }

        public async ValueTask SendStringPacketAndFlushAsync(PacketType packetType, PacketFlags flags, long requestId, string message, CancellationToken ct = default)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(packetType, flags, requestId))
            {
                writer.WriteUtf8String(message);
            }

            if (session is RpcSession rpcSession)
            {
                await rpcSession.SendPacketAndFlushAsync(writer, ct).ConfigureAwait(false);
                return;
            }

            session.SendPacket(writer);
        }

        public void SendPacketAsync(PacketType packetType, PacketFlags flags, long requestId)
        {
            var writer = BufferWriterPool.Get();
            writer.WritePacket(packetType, flags, requestId);
            session.SendPacket(writer);
        }

        public void SendRpcErrorAsync(long requestId, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            session.SendStringPacketAsync(
                PacketType.RpcResponse,
                PacketFlags.IsError,
                requestId,
                exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.ToPayloadMessage()
                    : exception.Message);
        }

        public void SendCancelAsync(long requestId)
            => session.SendPacketAsync(PacketType.Cancel, PacketFlags.None, requestId);

        public void SendStreamChunkAsync<T>(long requestId, sbyte streamId, T item)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamChunk, PacketFlags.None, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
                RpcCodec<T>.Codec.Serialize(item, writer);
            }

            session.SendPacket(writer);
        }

        public void SendStreamCompleteAsync(long requestId, sbyte streamId)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamComplete, PacketFlags.None, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
            }

            session.SendPacket(writer);
        }

        public void SendStreamErrorAsync(long requestId, sbyte streamId, string errorMessage)
        {
            var writer = BufferWriterPool.Get();
            using (writer.BeginPacketScope(PacketType.StreamError, PacketFlags.IsError, requestId))
            {
                var idSpan = writer.GetSpan(sizeof(sbyte));
                idSpan[0] = unchecked((byte)streamId);
                writer.Advance(sizeof(sbyte));
                writer.WriteUtf8String(errorMessage);
            }

            session.SendPacket(writer);
        }

        public void SendStreamErrorAsync(long requestId, sbyte streamId, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            session.SendStreamErrorAsync(
                requestId,
                streamId,
                exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.ToPayloadMessage()
                    : exception.Message);
        }
    }
}
