using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SharpLink.UnitTests.Runtime;

public class DateTimeOffsetCollectionCodecTests
{
    private static IRpcCodecProvider Codecs => RpcSessionTestFixture.RuntimeContext.Codecs;

    [Test]
    public void DateTimeOffsetCollectionsShouldShareOneLogicalCanonicalWire()
    {
        var value = new DateTimeOffset(2026, 7, 27, 12, 34, 56, TimeSpan.FromHours(8));
        var expected = CreateCanonicalPayload(value);

        Ensure(Serialize<DateTimeOffset[]?>([value]).SequenceEqual(expected), "array canonical wire");
        Ensure(Serialize<List<DateTimeOffset>?>([value]).SequenceEqual(expected), "list canonical wire");
        Ensure(Serialize(new Memory<DateTimeOffset>([value])).SequenceEqual(expected), "memory canonical wire");
        Ensure(Serialize(new ReadOnlyMemory<DateTimeOffset>([value])).SequenceEqual(expected), "readonly memory canonical wire");
        Ensure(Serialize(ImmutableArray.Create(value)).SequenceEqual(expected), "immutable array canonical wire");
    }

    [Test]
    public void DateTimeOffsetCollectionsShouldDecodeCanonicalWireAcrossSegments()
    {
        var expected = new DateTimeOffset(2026, 7, 27, 12, 34, 56, TimeSpan.FromHours(-5));
        var payload = CreateCanonicalPayload(expected);
        var segmented = CreateSegmentedSequence(payload);

        Ensure(Codecs.GetCodec<DateTimeOffset[]?>().Deserialize(segmented) is { Length: 1 } array && array[0] == expected,
            "segmented array canonical decode");
        Ensure(Codecs.GetCodec<List<DateTimeOffset>?>().Deserialize(segmented) is { Count: 1 } list && list[0] == expected,
            "segmented list canonical decode");
        Ensure(Codecs.GetCodec<Memory<DateTimeOffset>>().Deserialize(segmented).Span[0] == expected,
            "segmented memory canonical decode");
        Ensure(Codecs.GetCodec<ReadOnlyMemory<DateTimeOffset>>().Deserialize(segmented).Span[0] == expected,
            "segmented readonly memory canonical decode");
        Ensure(Codecs.GetCodec<ImmutableArray<DateTimeOffset>>().Deserialize(segmented)[0] == expected,
            "segmented immutable array canonical decode");
    }

    [Test]
    public void DateTimeOffsetCollectionsShouldRejectNonCanonicalPadding()
    {
        var payload = CreateCanonicalPayload(DateTimeOffset.UtcNow);
        payload[sizeof(int) + sizeof(short)] = 0xA5;

        try
        {
            _ = Codecs.GetCodec<DateTimeOffset[]?>().Deserialize(new ReadOnlySequence<byte>(payload));
            throw new Exception("expected DataLoss for non-canonical DateTimeOffset padding");
        }
        catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DataLoss)
        {
        }
    }

    private static byte[] Serialize<T>(in T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        Codecs.GetCodec<T>().Serialize(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] CreateCanonicalPayload(DateTimeOffset value)
    {
        var payload = new byte[sizeof(int) + 16];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 1);
        var element = payload.AsSpan(sizeof(int));
        BinaryPrimitives.WriteInt16LittleEndian(element, checked((short)value.Offset.TotalMinutes));
        element.Slice(sizeof(short), 6).Clear();
        BinaryPrimitives.WriteInt64LittleEndian(element.Slice(sizeof(long)), value.UtcTicks);
        return payload;
    }

    private static ReadOnlySequence<byte> CreateSegmentedSequence(byte[] bytes)
    {
        TestSequenceSegment? first = null;
        TestSequenceSegment? last = null;
        foreach (var value in bytes)
        {
            var segment = new TestSequenceSegment(new[] { value });
            if (first is null)
                first = segment;
            else
                last!.Append(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void Append(TestSequenceSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
