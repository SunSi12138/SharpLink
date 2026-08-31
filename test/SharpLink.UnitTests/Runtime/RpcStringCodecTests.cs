using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcStringCodecTests
{
    [Test]
    public void RootStringShouldUseUInt32Utf8Framing()
    {
        const string text = "A€𐍈";
        var expectedPayload = Encoding.UTF8.GetBytes(text);
        var writer = new ArrayBufferWriter<byte>();
        string? value = text;

        StringCodec.Instance.Serialize(in value, writer);

        Ensure(writer.WrittenCount == sizeof(uint) + expectedPayload.Length,
            "root string wire size must be a UInt32 byte length plus UTF-8 payload bytes");
        Ensure(BinaryPrimitives.ReadUInt32LittleEndian(writer.WrittenSpan) == (uint)expectedPayload.Length,
            "root string length prefix must contain the UTF-8 byte count");
        Ensure(writer.WrittenSpan[sizeof(uint)..].SequenceEqual(expectedPayload),
            "root string payload must be UTF-8 rather than native UTF-16 memory");

        var decoded = StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
        Ensure(decoded == text, "canonical UTF-8 root string payload must round-trip");
    }

    [Test]
    public void RootStringShouldReserveUIntMaxForNull()
    {
        var nullWriter = new ArrayBufferWriter<byte>();
        string? nullValue = null;
        StringCodec.Instance.Serialize(in nullValue, nullWriter);

        Ensure(nullWriter.WrittenCount == sizeof(uint),
            "root string null must contain only the UInt32 sentinel");
        Ensure(BinaryPrimitives.ReadUInt32LittleEndian(nullWriter.WrittenSpan) == uint.MaxValue,
            "root string null must use UInt32.MaxValue as the reserved sentinel");
        Ensure(StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(nullWriter.WrittenMemory)) is null,
            "UInt32.MaxValue root string sentinel must deserialize as null");

        var emptyWriter = new ArrayBufferWriter<byte>();
        string? emptyValue = string.Empty;
        StringCodec.Instance.Serialize(in emptyValue, emptyWriter);
        Ensure(BinaryPrimitives.ReadUInt32LittleEndian(emptyWriter.WrittenSpan) == 0,
            "empty string must remain distinct from null with a zero byte length");
        Ensure(StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(emptyWriter.WrittenMemory)) == string.Empty,
            "zero byte length must deserialize as an empty string");
    }

    [Test]
    public void RootStringShouldRejectInvalidUtf8()
    {
        var bytes = new byte[sizeof(uint) + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        bytes[sizeof(uint)] = 0xC3;
        bytes[sizeof(uint) + 1] = 0x28;

        try
        {
            _ = StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DataLoss)
        {
            return;
        }

        throw new InvalidOperationException("invalid UTF-8 root string payload must fail with DataLoss");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
