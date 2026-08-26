using SharpLink.IntegrationTests;

[assembly: RpcCodecAdapter(
    typeof(MalformedHeader),
    typeof(MalformedHeaderCodec),
    WireFormatId = "sharplink-integration-malformed-header/v1")]

namespace SharpLink.IntegrationTests;

public readonly record struct MalformedHeader(int Value);

public sealed class MalformedHeaderCodec : IRpcCodec<MalformedHeader>
{
    public void Serialize(in MalformedHeader value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value.Value);
        buffer.Advance(sizeof(int));
    }

    public MalformedHeader Deserialize(in ReadOnlySequence<byte> buffer)
        => throw new InvalidDataException("Injected request argument decode failure.");
}
