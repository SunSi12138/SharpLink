using System.Buffers.Binary;

namespace SharpLink.UnitTests.Runtime;

public sealed class DateTimeOffsetCollectionConstructionTests
{
    [Test]
    public void OffsetAndClockBoundaryOutcomesShouldMatchOriginalAlgorithm()
    {
        short[] offsets = [short.MinValue, -841, -840, -1, 0, 1, 840, 841, short.MaxValue];
        long[] ticks = [long.MinValue, -1, 0, 1, TimeSpan.TicksPerHour * 14 - 1,
            TimeSpan.TicksPerHour * 14, DateTime.UnixEpoch.Ticks,
            DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour * 14,
            DateTime.MaxValue.Ticks - 1, DateTime.MaxValue.Ticks,
            DateTime.MaxValue.Ticks + 1, long.MaxValue];
        foreach (var offset in offsets)
            foreach (var utc in ticks)
            {
                var bytes = new byte[20];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), offset);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), utc);
                string expected = Outcome(() => Original(utc, offset));
                for (var split = 0; split <= bytes.Length; split++)
                {
                    var sequence = Split(bytes, split);
                    string actual = Outcome(() => DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(sequence)[0]);
                    if (actual != expected)
                        throw new InvalidOperationException($"Boundary changed at {utc}, {offset}, {split}: {actual} vs {expected}");
                }
            }
    }

    private static DateTimeOffset Original(long utcTicks, short offsetMinutes)
    {
        if ((ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes is < -840 or > 840)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains invalid UTC ticks or offset.");
        var offsetTicks = (long)offsetMinutes * TimeSpan.TicksPerMinute;
        if (offsetTicks > 0 && utcTicks > DateTime.MaxValue.Ticks - offsetTicks ||
            offsetTicks < 0 && utcTicks < -offsetTicks)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DateTimeOffset collection contains a value outside the supported clock range.");
        try { return new DateTimeOffset(utcTicks + offsetTicks, TimeSpan.FromMinutes(offsetMinutes)); }
        catch (ArgumentException ex) { throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateTimeOffset payload.", ex); }
    }

    private static string Outcome(Func<DateTimeOffset> action)
    {
        try { var x = action(); return $"ok:{x.UtcTicks}:{x.Offset.Ticks}"; }
        catch (SharpLinkException ex) { return $"error:{ex.Code}:{ex.Message}:{ex.InnerException?.GetType().FullName}:{ex.InnerException?.Message}"; }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next; return next;
        }
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes, int split)
    {
        var first = new Segment(bytes.AsMemory(0, split)); var last = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
