namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    /// <summary>Publishes a stream error terminal and retires pre-credit/send-flow ownership.</summary>
    internal void SendStreamErrorAsync(
        long requestId,
        ushort streamId,
        SharpLinkException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var token = writer.BeginPacket(
                ProtocolV2FrameType.StreamComplete,
                ProtocolV2FrameFlags.Error,
                unchecked((ulong)requestId));
            var idSpan = writer.GetSpan(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
            writer.Advance(sizeof(ushort));
            ProtocolV2PayloadCodec.WriteError(
                writer,
                exception.Code,
                exception.Message,
                RuntimeContext.Protocol.MaxErrorMessageBytes,
                out var truncated);
            writer.EndPacket(token);
            if (truncated)
                writer.WrittenSpan[token.StartOffset + 6] |= (byte)ProtocolV2FrameFlags.Truncated;
            ownsWriter = false;
            try
            {
                SendPacket(writer);
            }
            finally
            {
                CompletePreCreditSendStream(requestId, streamId, exception);
                CompleteSendStream(requestId, streamId, exception);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }
}
