namespace SharpLink.Runtime;

internal static class RpcSessionContractManifestExtensions
{
    extension(RpcSession session)
    {
        internal async ValueTask SendContractManifestAndFlushAsync(
            ProtocolV2ContractManifest manifest,
            CancellationToken cancellationToken = default)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.ContractManifest,
                           ProtocolV2FrameFlags.None,
                           0))
                {
                    ProtocolV2ContractManifestCodec.Write(
                        writer,
                        manifest,
                        session.RuntimeContext.Protocol);
                }
                ownsWriter = false;
                await session.SendPacketAndFlushAsync(writer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal void SendContractManifest(ProtocolV2ContractManifest manifest)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.ContractManifest,
                           ProtocolV2FrameFlags.None,
                           0))
                {
                    ProtocolV2ContractManifestCodec.Write(
                        writer,
                        manifest,
                        session.RuntimeContext.Protocol);
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
    }
}
