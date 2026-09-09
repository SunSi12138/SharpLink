using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack4PacketHeaderTests
{
    [Test]
    [Arguments(32)]
    [Arguments(1024)]
    [Arguments(65536)]
    public void PacketScopeShouldInitializeOnlyCommittedHeader(int capacity)
    {
        using var writer = new PooledByteBufferWriter(capacity);
        writer.GetSpan(capacity).Fill(0xA5);
        using (writer.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 19))
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), 42);
            writer.Advance(8);
        }
        Check(writer.WrittenCount == 23, "packet length");
        Check(writer.WrittenSpan[0] == ProtocolV2Constants.Magic, "magic");
        Check(BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan[1..]) == 8, "payload length");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(writer.WrittenSpan[7..]) == 19, "request id");
        Check(BinaryPrimitives.ReadInt64LittleEndian(writer.WrittenSpan[15..]) == 42, "payload");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "uncommitted tail");
    }

    [Test]
    public void HeaderOnlyAndTokenPathsShouldInitializeLengthAndPreserveTail()
    {
        using var writer = new PooledByteBufferWriter(1024);
        writer.GetSpan(1024).Fill(0xA5);
        writer.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 7);
        Check(writer.WrittenCount == 15, "empty length");
        Check(BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan[1..]) == 0, "empty payload");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "empty tail");
        var token = writer.BeginPacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 8);
        writer.EndPacket(token);
        Check(writer.WrittenCount == 30, "second frame");
        Check(BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan[16..]) == 0, "second payload");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "second tail");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
