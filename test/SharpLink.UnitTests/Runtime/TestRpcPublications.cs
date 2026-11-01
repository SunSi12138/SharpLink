using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

// Host-only controls for actual Session/send-pump tests, not a model of the claim algorithm.
internal sealed class TestRpcBindingPublication(RpcSession session, ulong token = 1UL << 16)
    : RpcBindingPublication(session, token) { }

internal sealed class TestRpcStreamPublication(RpcBindingPublication binding, long requestId, ushort streamCount = 1)
    : RpcStreamPublication(binding, requestId, streamCount)
{
    internal TestRpcStreamPublication(RpcSession session, long requestId)
        : this(new TestRpcBindingPublication(session), requestId) { }

    internal void SendHead()
    {
        var writer = Session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, (ulong)RequestId))
        {
            var payload = writer.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(payload, Binding.Token);
            BinaryPrimitives.WriteInt64LittleEndian(payload[8..], 1);
            writer.Advance(16);
        }
        Session.SendPacket(writer, publication: this, publicationRole: RpcFramePublicationRole.RequestHead);
    }
}
