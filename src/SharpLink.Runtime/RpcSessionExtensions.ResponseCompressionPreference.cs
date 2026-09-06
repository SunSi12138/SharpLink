namespace SharpLink.Runtime;

internal static class RpcSessionResponseCompressionPreferenceExtensions
{
    extension(RpcSession session)
    {
        internal void SendResponseCompressionPreferenceUpdate(
            in ProtocolV2ResponseCompressionPreferenceUpdate update)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.ResponseCompressionPreferenceUpdate,
                           ProtocolV2FrameFlags.None,
                           0))
                {
                    ProtocolV2PayloadCodec.WriteResponseCompressionPreferenceUpdate(writer, update);
                }
                ownsWriter = false;
                session.SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal async ValueTask SendResponseCompressionPreferenceAckWithBackpressureAsync(
            ulong appliedGeneration,
            CancellationToken cancellationToken)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.ResponseCompressionPreferenceAck,
                           ProtocolV2FrameFlags.None,
                           0))
                {
                    ProtocolV2PayloadCodec.WriteResponseCompressionPreferenceAck(
                        writer,
                        new ProtocolV2ResponseCompressionPreferenceAck(appliedGeneration));
                }
                ownsWriter = false;
                await session.SendPacketWithBackpressureAsync(writer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }
    }
}
