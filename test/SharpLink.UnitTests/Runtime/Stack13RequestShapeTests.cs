using SharpLink.Sdk;
using System.Linq;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13RequestShapeTests
{
    [Test]
    public void FixedRequestValidationShouldRetainRoutingAndBudgetTruncationChecks()
    {
        foreach (var timed in new[] { false, true })
        {
            for (var length = 0; length <= 32; length++)
            {
                var bytes = Frame(new byte[length], timed ? ProtocolV2FrameFlags.HasTimeBudget : 0);
                for (var split = 0; split <= bytes.Length; split++)
                {
                    var buffer = Split(bytes, split);
                    var start = buffer.Start;
                    try
                    {
                        var ok = ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out _, out var payload);
                        Check(length >= (timed ? 24 : 16) && ok && payload.Length == length && buffer.IsEmpty, "fixed request valid boundary");
                    }
                    catch (SharpLinkException exception)
                    {
                        Check(length < (timed ? 24 : 16), "unexpected fixed validation error");
                        Check(exception.Code == SharpLinkErrorCode.ProtocolViolation && buffer.Start.Equals(start), "error without source consumption");
                    }
                }
            }
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
