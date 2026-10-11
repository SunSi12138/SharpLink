namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static async ValueTask DrainTerminalStreamProgressAsync(
        RpcSession session,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        long requestId,
        ReadOnlySequence<byte> payload,
        bool decoded)
    {
        if (!decoded)
            session.ValidateInboundPayloadEnvelope(type, flags, payload);
        if (type == ProtocolV2FrameType.StreamData)
        {
            var encodedBytes = !decoded && (flags & ProtocolV2FrameFlags.Compressed) != 0
                ? RpcSession.ReadCompressedOriginalLength(type, flags, payload)
                : Math.Max(1, checked((int)payload.Length - sizeof(ushort)));
            var streamId = TryReadStreamId(ref payload);
            if (session.StreamManager.TryDiscardTerminalStreamData(
                    requestId, streamId, payload, encodedBytes, out var dispatch))
                await dispatch.ConfigureAwait(false);
            return;
        }

        var completedStreamId = TryReadStreamId(ref payload);
        Exception? exception = null;
        if ((flags & ProtocolV2FrameFlags.Error) != 0)
        {
            var error = ProtocolV2PayloadCodec.ReadError(payload, flags,
                session.RuntimeContext.Protocol.MaxErrorMessageBytes);
            exception = new SharpLinkException(error.Code, error.Message);
        }
        session.StreamManager.TryCompleteTerminalStream(requestId, completedStreamId, exception);
    }
}
