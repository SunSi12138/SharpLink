using SharpLink.Sdk;
using System.Linq;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13HeaderSpanTests
{
    [Test]
    public void ParsingFirstFrameShouldNotConsumeSecondFrameAtAnySplit()
    {
        var a = Frame(new byte[24], ProtocolV2FrameFlags.HasReturn);
        var b = Frame(new byte[16], 0);
        var bytes = a.Concat(b).ToArray();
        for (var split = 0; split <= bytes.Length; split++)
        {
            var buffer = Split(bytes, split);
            Check(ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out _, out var payload), "first complete");
            Check(payload.Length == 24 && buffer.Length == b.Length, "second preserved");
            Check(ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out _, out payload), "second complete");
            Check(payload.Length == 16 && buffer.IsEmpty, "second consumed exactly");
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
