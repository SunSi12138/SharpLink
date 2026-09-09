using SharpLink.Sdk;
using System.Linq;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13MetadataReadTests
{
    [Test]
    public void MetadataShouldDecodeAcrossEveryUtf8BoundaryAndRejectBadEncoding()
    {
        var expected = new SharpLinkMetadata(new KeyValuePair<string, string>("租户🙂", "かな-汉字-🙂"));
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteMetadata(writer, expected);
        var bytes = writer.WrittenSpan.ToArray();
        for (var split = 0; split <= bytes.Length; split++)
        {
            var result = ProtocolV2PayloadCodec.ReadMetadata(Split(bytes, split));
            Check(result.Count == 1 && result[0].Equals(expected[0]), "metadata boundary roundtrip");
        }
        var invalid = new byte[] { 1, 1, (byte)'k', 3, 0xed, 0xa0, 0x80 };
        for (var split = 0; split <= invalid.Length; split++)
        {
            try { _ = ProtocolV2PayloadCodec.ReadMetadata(Split(invalid, split)); throw new Exception("invalid UTF8 accepted"); }
            catch (SharpLinkException exception) { Check(exception.Code == SharpLinkErrorCode.ProtocolViolation, "strict UTF8 protocol error"); }
        }
    }

    private static byte[] Frame(byte[] payload, ProtocolV2FrameFlags flags)
    {
        var a = new byte[15 + payload.Length]; a[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(1), payload.Length);
        a[5] = (byte)ProtocolV2FrameType.Request; a[6] = (byte)flags;
        BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(7), 1); payload.CopyTo(a, 15); return a;
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes, int split)
    {
        var first = new Segment(bytes.AsMemory(0, split)); var last = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment(ReadOnlyMemory<byte> memory) : ReadOnlySequenceSegment<byte>
    {
        private bool _initialized;
        internal Segment Append(ReadOnlyMemory<byte> next)
        {
            if (!_initialized) { Memory = memory; _initialized = true; }
            var last = new Segment(next) { Memory = next, RunningIndex = RunningIndex + Memory.Length, _initialized = true };
            Next = last; return last;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
