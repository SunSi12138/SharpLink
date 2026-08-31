namespace SharpLink.UnitTests.Abstractions;

public class GeneratedCodecWireTests
{
    [Test]
    public void UnknownFieldsShouldSkipAcrossSegments()
    {
        using var writer = new PooledByteBufferWriter(16);
        RpcGeneratedCodecWire.WriteFieldKey(writer, 17, RpcGeneratedWireType.Fixed8);
        RpcGeneratedCodecWire.WriteUnmanaged(writer, 42L);
        RpcGeneratedCodecWire.WriteFieldKey(writer, 18, RpcGeneratedWireType.LengthDelimited);
        RpcGeneratedCodecWire.WriteString(writer, "sharp");
        RpcGeneratedCodecWire.WriteObjectEnd(writer);

        var bytes = writer.WrittenMemory.ToArray();
        var sequence = CreateSegmentedSequence(bytes);
        var reader = new SequenceReader<byte>(sequence);
        Ensure(RpcGeneratedCodecWire.TryReadField(ref reader, out _, out var fixedType), "fixed field");
        RpcGeneratedCodecWire.SkipField(ref reader, fixedType);
        Ensure(RpcGeneratedCodecWire.TryReadField(ref reader, out _, out var stringType), "string field");
        RpcGeneratedCodecWire.SkipField(ref reader, stringType);
        Ensure(!RpcGeneratedCodecWire.TryReadField(ref reader, out _, out _), "object end");
        RpcGeneratedCodecWire.EnsureFullyConsumed(reader);
    }

    [Test]
    public void TruncatedLengthAndOversizedCollectionShouldFailStructurally()
    {
        var truncated = CaptureTruncatedLength();
        Ensure(truncated.Code == SharpLinkErrorCode.DataLoss, "truncated length code");

        using var writer = new PooledByteBufferWriter();
        var exhausted = CaptureSharpLink(() => RpcGeneratedCodecWire.WriteCollectionCount(
            writer,
            RpcGeneratedCodecWire.MaximumCollectionItems + 1,
            false));
        Ensure(exhausted.Code == SharpLinkErrorCode.ResourceExhausted, "collection limit code");
    }

    [Test]
    public void GeneratedStringWriterShouldPreserveIsolatedSurrogates()
    {
        using var writer = new PooledByteBufferWriter();
        var source = new string(['\uD800', 'X', '\uDC00']);

        RpcGeneratedCodecWire.WriteString(writer, source);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(writer.WrittenMemory));
        var decoded = RpcGeneratedCodecWire.ReadString(ref reader);

        Ensure(decoded == source, "generated v2 string wire must preserve arbitrary UTF-16 code units");
        Ensure(reader.Remaining == 0, "generated string reader must consume the full UTF-16 payload");
    }

    private static SharpLinkException CaptureSharpLink(Action action)
    {
        try
        {
            action();
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static SharpLinkException CaptureTruncatedLength()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 5, 0, 0, 0, 1 }));
        try
        {
            _ = RpcGeneratedCodecWire.ReadLengthDelimited(ref reader);
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static ReadOnlySequence<byte> CreateSegmentedSequence(byte[] bytes)
    {
        TestSegment? first = null;
        TestSegment? last = null;
        foreach (var value in bytes)
        {
            var segment = new TestSegment(new[] { value });
            if (first is null)
                first = segment;
            else
                last!.Append(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void Append(TestSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
