namespace SharpLink.UnitTests.Runtime;

public sealed class Stack13ElapsedBoundaryTests
{
    [Test]
    public void NarrowElapsedPathAndFallbackShouldMatchWideReference()
    {
        const ulong threshold = ulong.MaxValue / 10_000_000;
        foreach (var frequency in new long[] { 1, 32768, 10_000_000, 1_000_000_000, long.MaxValue })
        {
            foreach (var units in new ulong[] { 0, 1, threshold - 1, threshold, threshold + 1, (ulong)long.MaxValue, ulong.MaxValue })
            {
                foreach (var origin in new long[] { 0, long.MaxValue - 3 })
                {
                    var ticks = (UInt128)units * 10_000_000 / (ulong)frequency;
                    var expected = ticks >= (UInt128)long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
                    var actual = SharpLinkTime.GetElapsed(origin, unchecked(origin + (long)units), frequency);
                    if (actual != expected)
                        throw new InvalidOperationException("Elapsed arithmetic differs at fast/fallback boundary.");
                }
            }
        }
    }
}
