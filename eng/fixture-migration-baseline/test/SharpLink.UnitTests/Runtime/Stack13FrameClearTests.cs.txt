using SharpLink.Sdk;
using System.Linq;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13FrameClearTests
{
    [Test]
    [Arguments(32)]
    [Arguments(1024)]
    [Arguments(65536)]
    public void FrameInitializationShouldTouchOnlyItsOwnFifteenBytes(int capacity)
    {
        using var writer = new PooledByteBufferWriter(capacity);
        writer.GetSpan(capacity).Fill(0xA5);
        writer.Advance(5);
        var token = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.Response, 0, ulong.MaxValue);
        Check(writer.WrittenCount == 20, "header committed length");
        Check(writer.WrittenSpan[..5].IndexOfAnyExcept((byte)0xA5) < 0, "prior bytes unchanged");
        Check(writer.GetSpan().IndexOfAnyExcept((byte)0xA5) < 0, "uncommitted tail must not be cleared");
        ProtocolV2FrameWriter.EndFrame(writer, token);
        var header = writer.WrittenSpan[5..];
        Check(header[0] == ProtocolV2Constants.Magic, "magic");
        Check(BinaryPrimitives.ReadInt32LittleEndian(header[1..]) == 0, "empty length");
        Check(header[5] == (byte)ProtocolV2FrameType.Response && header[6] == 0, "type and flags");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(header[7..]) == ulong.MaxValue, "id");
    }

    [Test]
    public void ExactWriterLimitAndDisposedLeaseMustRemainEnforced()
    {
        using var pool = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var writer = pool.Rent(15);
        ProtocolV2FrameWriter.WriteEmptyFrame(writer, ProtocolV2FrameType.Cancel, 0, 9);
        Check(writer.WrittenCount == 15, "exact-size header");
        try { writer.GetSpan(1); throw new Exception("writer limit bypassed"); }
        catch (SharpLinkException exception) { Check(exception.Code == SharpLinkErrorCode.ResourceExhausted, "lease limit"); }
        pool.Return(writer);
        try { _ = writer.WrittenCount; throw new Exception("disposed lease exposed"); }
        catch (ObjectDisposedException) { }
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
