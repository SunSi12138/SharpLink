using SharpLink.IntegrationTests;

[assembly: RpcCodec(
    typeof(MalformedHeader),
    typeof(MalformedHeaderCodec))]

namespace SharpLink.IntegrationTests;

public readonly record struct MalformedHeader(int Value);

[RpcCodecImplementation(
    "sharplink-integration-malformed-header/v1",
    "sharplink-integration-malformed-header-schema/v1")]
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
