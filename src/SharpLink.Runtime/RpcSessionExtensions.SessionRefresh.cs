namespace SharpLink.Runtime;

internal static class RpcSessionSessionRefreshExtensions
{
    extension(RpcSession session)
    {
        internal async ValueTask SendSessionRefreshRequestedWithBackpressureAsync(
            ProtocolV2SessionRefreshRequested request,
            CancellationToken cancellationToken)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
                return;

            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.SessionRefreshRequested,
                           ProtocolV2FrameFlags.None,
                           0))
                {
                    ProtocolV2PayloadCodec.WriteSessionRefreshRequested(writer, request);
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
