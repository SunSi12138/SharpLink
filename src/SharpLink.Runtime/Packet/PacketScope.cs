namespace SharpLink.Runtime;

internal readonly record struct PacketToken(int StartOffset);

internal readonly ref struct PacketScope(IRpcByteBufferWriter writer, PacketToken token)
{
    public void Dispose()
        => ProtocolV2FrameWriter.EndFrame(writer, new ProtocolV2FrameToken(token.StartOffset));
}
