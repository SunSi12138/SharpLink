namespace SharpLink.Runtime;

public static partial class ProtocolV2PayloadCodec
{
    /// <summary>Writes one fixed-size response-compression preference update.</summary>
    public static void WriteResponseCompressionPreferenceUpdate(
        IBufferWriter<byte> writer,
        in ProtocolV2ResponseCompressionPreferenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteUInt64(writer, update.Generation);
        WriteByte(writer, update.AllowResponseCompression ? (byte)1 : (byte)0);
    }

    /// <summary>Reads one complete fixed-size response-compression preference update.</summary>
    public static ProtocolV2ResponseCompressionPreferenceUpdate ReadResponseCompressionPreferenceUpdate(
        ReadOnlySequence<byte> payload)
    {
        if (payload.Length != sizeof(ulong) + sizeof(byte))
            throw ProtocolV2FrameParser.Violation("ResponseCompressionPreferenceUpdate payload must contain UInt64 generation and one preference byte.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long generationBits) || !reader.TryRead(out var preference) || preference > 1)
            throw ProtocolV2FrameParser.Violation("ResponseCompressionPreferenceUpdate payload is invalid.");
        return new ProtocolV2ResponseCompressionPreferenceUpdate(
            unchecked((ulong)generationBits),
            preference != 0);
    }

    /// <summary>Writes one cumulative response-compression preference acknowledgement.</summary>
    public static void WriteResponseCompressionPreferenceAck(
        IBufferWriter<byte> writer,
        in ProtocolV2ResponseCompressionPreferenceAck ack)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteUInt64(writer, ack.AppliedGeneration);
    }

    /// <summary>Reads one complete cumulative response-compression preference acknowledgement.</summary>
    public static ProtocolV2ResponseCompressionPreferenceAck ReadResponseCompressionPreferenceAck(
        ReadOnlySequence<byte> payload)
    {
        if (payload.Length != sizeof(ulong))
            throw ProtocolV2FrameParser.Violation("ResponseCompressionPreferenceAck payload must contain one UInt64 generation.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long generationBits))
            throw ProtocolV2FrameParser.Violation("ResponseCompressionPreferenceAck payload is truncated.");
        return new ProtocolV2ResponseCompressionPreferenceAck(unchecked((ulong)generationBits));
    }

}
