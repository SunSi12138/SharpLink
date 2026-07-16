namespace SharpLink.Runtime;

public readonly record struct PacketToken(int StartOffset);

public readonly ref struct PacketScope(ArrayBufferWriter<byte> writer, PacketToken token)
{
    public void Dispose()
    {
        var bodyLength = writer.WrittenCount - token.StartOffset - ProtocolV2Constants.HeaderBytes;
        var span = MemoryMarshal.AsMemory(writer.WrittenMemory).Span;
        var lengthSlice = span.Slice(token.StartOffset + 1, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(lengthSlice, bodyLength);
    }
}
