using SharpLink.IntegrationTests;

[assembly: RpcCodecAdapter(
    typeof(IntegrationPersonCodec),
    typeof(IntegrationPersonCodec),
    WireFormatId = "sharplink-integration-person/v1")]

namespace SharpLink.IntegrationTests;

public sealed class IntegrationPersonCodec : IRpcCodec<IntegrationPersonCodec>
{
    public void Serialize(in IntegrationPersonCodec value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(1);
        span[0] = 0;
        buffer.Advance(1);
    }

    public IntegrationPersonCodec Deserialize(in ReadOnlySequence<byte> buffer)
        => throw new InvalidDataException("Injected request argument decode failure.");
}
