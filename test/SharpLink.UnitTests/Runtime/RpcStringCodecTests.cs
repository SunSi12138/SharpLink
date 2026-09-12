using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcStringCodecTests
{
    [Test]
    public void RootStringShouldPreserveInt32Utf16Framing()
    {
        const string text = "A€𐍈";
        var expectedPayload = MemoryMarshal.AsBytes(text.AsSpan()).ToArray();
        var writer = new ArrayBufferWriter<byte>();
        string? value = text;

        StringCodec.Instance.Serialize(in value, writer);

        Ensure(writer.WrittenCount == sizeof(int) + expectedPayload.Length,
            "root string wire size must remain a signed Int32 byte length plus UTF-16 payload bytes");
        Ensure(BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan) == expectedPayload.Length,
            "root string length prefix must contain the UTF-16 byte count");
        Ensure(writer.WrittenSpan[sizeof(int)..].SequenceEqual(expectedPayload),
            "root string payload must preserve the v2 UTF-16 code units");

        var decoded = StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
        Ensure(decoded == text, "v2 root string payload must round-trip");
    }

    [Test]
    public void RootStringShouldReserveMinusOneForNull()
    {
        var nullWriter = new ArrayBufferWriter<byte>();
        string? nullValue = null;
        StringCodec.Instance.Serialize(in nullValue, nullWriter);

        Ensure(nullWriter.WrittenCount == sizeof(int),
            "root string null must contain only the signed Int32 sentinel");
        Ensure(BinaryPrimitives.ReadInt32LittleEndian(nullWriter.WrittenSpan) == -1,
            "root string null must preserve the v2 -1 sentinel");
        Ensure(StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(nullWriter.WrittenMemory)) is null,
            "the -1 root string sentinel must deserialize as null");

        var emptyWriter = new ArrayBufferWriter<byte>();
        string? emptyValue = string.Empty;
        StringCodec.Instance.Serialize(in emptyValue, emptyWriter);
        Ensure(BinaryPrimitives.ReadInt32LittleEndian(emptyWriter.WrittenSpan) == 0,
            "empty string must remain distinct from null with a zero byte length");
        Ensure(StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(emptyWriter.WrittenMemory)) == string.Empty,
            "zero byte length must deserialize as an empty string");
    }

    [Test]
    public void RootStringShouldPreserveArbitraryUtf16CodeUnits()
    {
        var text = new string(['\uD800', 'X', '\uDC00']);
        var writer = new ArrayBufferWriter<byte>();
        string? value = text;

        StringCodec.Instance.Serialize(in value, writer);
        var decoded = StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(decoded == text,
            "v2 root string wire must preserve arbitrary .NET UTF-16 code units, including unpaired surrogates");
    }

    [Test]
    public void RootStringShouldRejectOddUtf16ByteLength()
    {
        var bytes = new byte[sizeof(int) + 1];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);

        try
        {
            _ = StringCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DataLoss)
        {
            return;
        }

        throw new InvalidOperationException("odd UTF-16 root string byte length must fail with DataLoss");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
