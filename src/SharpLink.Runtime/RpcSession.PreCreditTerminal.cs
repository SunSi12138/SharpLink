namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    /// <summary>
    /// Instance members shadow the extension-frame helpers so stream terminal transitions can reject
    /// budget waiters that have not yet reached StreamFlowController admission.
    /// </summary>
    internal void SendStreamCompleteAsync(long requestId, ushort streamId)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamComplete,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
            }
            ownsWriter = false;
            try
            {
                SendPacket(writer);
            }
            finally
            {
                CompletePreCreditSendStream(requestId, streamId);
                CompleteSendStream(requestId, streamId);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

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
