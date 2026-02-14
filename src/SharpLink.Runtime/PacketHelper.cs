namespace SharpLink.Runtime;

public static class PacketHelper
{
    public static bool TryReadMessage(ref ReadOnlySequence<byte> buffer,out PacketHeader header, out ReadOnlySequence<byte> payload)
    {
        payload = default;
        header = default;

        if (buffer.Length < ProtocolConstants.HeaderBytes) return false;

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryRead(out var magic))
            return false;

        if (magic != ProtocolConstants.MagicNumber)
            throw new InvalidDataException("Bad Magic");

        if (!reader.TryReadLittleEndian(out int packetLength))
            return false;

        if (!reader.TryRead(out var typeRaw))
            return false;

        if (!reader.TryRead(out var flagsRaw))
            return false;

        if (!reader.TryReadLittleEndian(out long requestId))
            return false;

        if (packetLength < 0 || reader.Remaining < packetLength) return false; // half packet, wait for more data

        payload = buffer.Slice(ProtocolConstants.HeaderBytes, packetLength);
        header = new PacketHeader((PacketType)typeRaw, (PacketFlags)flagsRaw, requestId);
        buffer = buffer.Slice(ProtocolConstants.HeaderBytes + packetLength);

        return true;
    }
}
