namespace SharpLink.Runtime;

/// <summary>Streaming helpers used by generated artifacts with construction-time-bound Codecs.</summary>
public static class RpcBoundStreamExtensions
{
    /// <summary>Sends one stream item using the Codec selected for the owning generated Contract.</summary>
    public static async ValueTask SendBoundStreamChunkAsync<T>(
        this IRpcSession session,
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(codec);
        var runtimeSession = session as RpcSession ?? throw new InvalidOperationException(
            "SharpLink generated streams require the built-in runtime session implementation.");
        var writer = runtimeSession.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }
            var encodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            await runtimeSession.AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken).ConfigureAwait(false);
            try
            {
                ownsWriter = false;
                runtimeSession.SendPacket(writer);
            }
            catch
            {
                runtimeSession.ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                throw;
            }
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }
}
