using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcGeneratedCodecWireIdentityRegressionTests
{
    [Test]
    public void DateTimeOffsetShouldUseCanonicalLogicalLayout()
    {
        var value = new DateTimeOffset(2026, 8, 31, 9, 12, 13, TimeSpan.FromMinutes(330));
        var writer = new ArrayBufferWriter<byte>();

        RpcGeneratedCodecWire.WriteDateTimeOffset(writer, value);

        Ensure(writer.WrittenCount == 16, "generated DateTimeOffset must remain a 16-byte fixed payload");
        var payload = writer.WrittenSpan;
        Ensure(BinaryPrimitives.ReadInt16LittleEndian(payload) == 330,
            "generated DateTimeOffset must write logical offset minutes at bytes 0..1");
        for (var index = sizeof(short); index < sizeof(long); index++)
            Ensure(payload[index] == 0, "generated DateTimeOffset padding bytes 2..7 must be canonical zero");
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(long)..]) == value.UtcDateTime.Ticks,
            "generated DateTimeOffset must write logical UTC ticks at bytes 8..15");

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(writer.WrittenMemory));
        var decoded = RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader);
        Ensure(decoded.Equals(value), "canonical generated DateTimeOffset payload must round-trip");
        Ensure(reader.Remaining == 0, "generated DateTimeOffset reader must consume exactly 16 bytes");
    }

    [Test]
    public void DateTimeOffsetShouldRejectNonCanonicalPadding()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, 0);
        bytes[2] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(sizeof(long)), DateTime.UnixEpoch.Ticks);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));

        try
        {
            _ = RpcGeneratedCodecWire.ReadDateTimeOffset(ref reader);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DataLoss)
        {
            return;
        }

        throw new InvalidOperationException("non-zero generated DateTimeOffset padding must fail with DataLoss");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
