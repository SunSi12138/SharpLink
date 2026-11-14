namespace SharpLink.Runtime;

internal static partial class StaticBindingWire
{
    internal const ProtocolV2FrameType RequestAccepted = ProtocolV2FrameType.RequestAccepted;
    internal static IRpcByteBufferWriter CreateRequestAccepted(RpcSession session, long requestId)
    {
        var writer = session.RentFrameWriter();
        try { using (writer.BeginPacketScope(RequestAccepted, ProtocolV2FrameFlags.None, unchecked((ulong)requestId))) { } return writer; }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
    }
}
