using SharpLink.Sdk;
using System.Linq;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13NullableClearTests
{
    [Test]
    [Arguments(32)]
    [Arguments(1024)]
    [Arguments(65536)]
    public void NullCodecsShouldZeroCanonicalBytesAndLeaveUnusedCapacityAlone(int capacity)
    {
        using var writer = new PooledByteBufferWriter(capacity);
        double? d = null;
        float? f = null;
        Guid? g = null;
        writer.GetSpan(capacity).Fill(0xA5);
        NullableDoubleCodec.Instance.Serialize(in d, writer);
        Check(writer.WrittenCount == 9 && writer.WrittenSpan.IndexOfAnyExcept((byte)0) < 0, "canonical double null");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "double tail");
        writer.Clear(); writer.GetSpan(capacity).Fill(0xA5);
        NullableFloatCodec.Instance.Serialize(in f, writer);
        Check(writer.WrittenCount == 5 && writer.WrittenSpan.IndexOfAnyExcept((byte)0) < 0, "canonical float null");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "float tail");
        writer.Clear(); writer.GetSpan(capacity).Fill(0xA5);
        NullableGuidCodec.Instance.Serialize(in g, writer);
        Check(writer.WrittenCount == 17 && writer.WrittenSpan.IndexOfAnyExcept((byte)0) < 0, "canonical guid null");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "guid tail");
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
