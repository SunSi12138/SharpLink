using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedUnionCodecTests
{
    [Test]
    public void GeneratedUnionCodecShouldRoundTripCasesNullAndNestedCollections()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<IGeneratedUnionPayload>();

        var text = new GeneratedUnionTextCase { Value = "alpha" };
        var textBytes = Serialize(codec, text);
        Ensure(textBytes.Length > 1 && textBytes[0] == 1,
            "text case must use discriminator 1 before the child payload");
        var decodedText = codec.Deserialize(new ReadOnlySequence<byte>(textBytes));
        Ensure(decodedText is GeneratedUnionTextCase { Value: "alpha" },
            "text union case must round-trip through the generated Codec");

        var number = new GeneratedUnionNumberCase { Value = 42 };
        var numberBytes = Serialize(codec, number);
        Ensure(numberBytes.Length > 1 && numberBytes[0] == 2,
            "number case must use discriminator 2 before the child payload");
        var decodedNumber = codec.Deserialize(new ReadOnlySequence<byte>(numberBytes));
        Ensure(decodedNumber is GeneratedUnionNumberCase { Value: 42 },
            "number union case must round-trip through the generated Codec");

        IGeneratedUnionPayload? noValue = null;
        var nullBytes = Serialize(codec, noValue!);
        Ensure(nullBytes.Length == 1 && nullBytes[0] == 0,
            "union null must use the reserved discriminator 0 without a child payload");
        Ensure(codec.Deserialize(new ReadOnlySequence<byte>(nullBytes)) is null,
            "union discriminator 0 must decode as null");

        var envelopeCodec = context.Codecs.GetCodec<GeneratedUnionEnvelope>();
        var envelope = new GeneratedUnionEnvelope
        {
            Value = text,
            Items =
            [
                new GeneratedUnionNumberCase { Value = 7 },
                new GeneratedUnionTextCase { Value = "nested" }
            ]
        };
        var envelopeBytes = Serialize(envelopeCodec, envelope);
        var decodedEnvelope = envelopeCodec.Deserialize(new ReadOnlySequence<byte>(envelopeBytes));
        Ensure(decodedEnvelope is not null &&
               decodedEnvelope.Value is GeneratedUnionTextCase { Value: "alpha" } &&
               decodedEnvelope.Items.Count == 2 &&
               decodedEnvelope.Items[0] is GeneratedUnionNumberCase { Value: 7 } &&
               decodedEnvelope.Items[1] is GeneratedUnionTextCase { Value: "nested" },
            "DTO and collection nesting must resolve the same generated union Codec");
    }

    [Test]
    public void GeneratedUnionCodecShouldRejectMalformedDiscriminatorsAsDataLoss()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<IGeneratedUnionPayload>();

        ExpectDataLoss(codec, [3]);
        ExpectDataLoss(codec, [0, 1]);
        ExpectDataLoss(codec, [0x81, 0x00]);
        ExpectDataLoss(codec, [0x80]);
        ExpectDataLoss(codec, [0xFF, 0xFF, 0xFF, 0xFF, 0x10]);
    }

    private static byte[] Serialize<T>(IRpcCodec<T> codec, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void ExpectDataLoss(IRpcCodec<IGeneratedUnionPayload> codec, byte[] payload)
    {
        try
        {
            _ = codec.Deserialize(new ReadOnlySequence<byte>(payload));
            throw new Exception("Expected malformed native union payload to fail with DataLoss.");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DataLoss)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

[RpcSerializable]
public sealed class GeneratedUnionEnvelope
{
    public IGeneratedUnionPayload? Value { get; set; }
    public List<IGeneratedUnionPayload> Items { get; set; } = [];
}

[RpcUnionCase(2, typeof(GeneratedUnionNumberCase))]
[RpcUnionCase(1, typeof(GeneratedUnionTextCase))]
public interface IGeneratedUnionPayload
{
}

public sealed class GeneratedUnionTextCase : IGeneratedUnionPayload
{
    public string Value { get; set; } = string.Empty;
}

public sealed class GeneratedUnionNumberCase : IGeneratedUnionPayload
{
    public int Value { get; set; }
}
