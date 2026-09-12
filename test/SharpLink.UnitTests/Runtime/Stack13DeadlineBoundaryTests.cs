namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13DeadlineBoundaryTests
{
    [Test]
    public void CachedExpiryShouldMatchRemainingAtRoundingAndWrapBoundaries()
    {
        foreach (var frequency in new long[] { 1, 32768, 10_000_000, 1_000_000_000 })
        {
            foreach (var ticks in new long[] { 0, 1, 17, 10_000_001, 300_000_000 })
            {
                foreach (var origin in new long[] { 0, -17, long.MaxValue - 7, long.MinValue })
                {
                    var deadline = RpcDeadline.Create(TimeSpan.FromTicks(ticks), origin, frequency);
                    var units = (ulong)(((UInt128)(ulong)ticks * (ulong)frequency + 9_999_999) / 10_000_000);
                    foreach (var elapsed in new ulong[] { 0, units == 0 ? 0 : units - 1, units, units + 1 })
                    {
                        var clock = new Clock(unchecked(origin + (long)elapsed), frequency);
                        if (deadline.IsExpired(clock) != (deadline.GetRemaining(clock) == TimeSpan.Zero))
                            throw new InvalidOperationException("Expiry diverged from remaining budget.");
                    }
                    var expected = SharpLinkTime.AddDuration(origin, TimeSpan.FromTicks(ticks), frequency);
                    if (deadline.Timestamp != expected)
                        throw new InvalidOperationException("Diagnostic timestamp changed.");
                }
            }
        }
    }

    private sealed class Clock(long now, long frequency) : TimeProvider
    {
        public override long GetTimestamp() => now;
        public override long TimestampFrequency => frequency;
    }
}
