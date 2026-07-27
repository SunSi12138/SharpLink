namespace SharpLink.Runtime;

internal readonly record struct PacketToken(int StartOffset);

internal readonly ref struct PacketScope(IRpcByteBufferWriter writer, PacketToken token)
{
    public void Dispose()
    {
        var bodyLength = writer.WrittenCount - token.StartOffset - ProtocolV2Constants.HeaderBytes;
        var span = writer.WrittenSpan;
        var lengthSlice = span.Slice(token.StartOffset + 1, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(lengthSlice, bodyLength);
    }
}
