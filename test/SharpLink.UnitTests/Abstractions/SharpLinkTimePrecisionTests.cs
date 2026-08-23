namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkTimePrecisionTests
{
    [Test]
    public void GetRemainingShouldPreserveOneUnitNearLongMaxValue()
    {
        var remaining = SharpLinkTime.GetRemaining(
            long.MaxValue,
            long.MaxValue - 1,
            TimeSpan.TicksPerSecond);
        Ensure(remaining == TimeSpan.FromTicks(1),
            "one positive timestamp unit must never round down to expired");
    }

    [Test]
    public void AddDurationShouldRoundUpAtFrequencyAboveDoubleIntegerPrecision()
    {
        const long frequency = 9_007_199_254_740_993L; // 2^53 + 1
        var deadline = SharpLinkTime.AddDuration(0, TimeSpan.FromSeconds(1), frequency);
        Ensure(deadline == frequency,
            "one second must resolve to the exact custom-provider frequency");
    }

    [Test]
    public void RoundTripShouldNeverExpireEarlyAtExtremeValues()
    {
        const long frequency = 9_007_199_254_740_993L;
        var start = long.MaxValue - frequency - 10;
        var deadline = SharpLinkTime.AddDuration(start, TimeSpan.FromSeconds(1), frequency);
        var remaining = SharpLinkTime.GetRemaining(deadline, start, frequency);
        Ensure(remaining >= TimeSpan.FromSeconds(1),
            "duration -> timestamp -> duration conversion must not shorten the lifetime");
    }

    [Test]
    public void AddDurationShouldNotSaturateWhenNegativeTimestampPlusLargeDeltaFits()
    {
        var deadline = SharpLinkTime.AddDuration(
            long.MinValue,
            TimeSpan.FromSeconds(1),
            long.MaxValue);
        Ensure(deadline == -1,
            "widened final addition must preserve a representable negative-start result");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
