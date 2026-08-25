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
    public void WouldExpireBeforeOrAtShouldNotRoundProspectiveDelayUpAtLowFrequency()
    {
        var provider = new FixedTimestampTimeProvider(timestamp: 0, frequency: 1);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), provider);

        Ensure(!deadline.WouldExpireBeforeOrAt(TimeSpan.FromTicks(1), provider),
            "a 100ns prospective delay must not consume a full one-second timestamp unit");
        Ensure(deadline.WouldExpireBeforeOrAt(TimeSpan.FromSeconds(1), provider),
            "a delay that reaches the exact monotonic boundary must be rejected");
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
    public void RoundTripShouldPreserveFullLifetimeAcrossSignedTimestampBoundary()
    {
        const long frequency = TimeSpan.TicksPerSecond;
        var start = long.MaxValue - 5_000_000;
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), start, frequency);

        Ensure(deadline.Timestamp < 0,
            "a finite deadline that crosses long.MaxValue must wrap into the signed-negative half");
        Ensure(RpcDeadline.GetRemaining(deadline.Timestamp, start, frequency) == TimeSpan.FromSeconds(1),
            "crossing the signed boundary must preserve the full configured lifetime");

        var oneTickBefore = unchecked(start + frequency - 1);
        Ensure(!deadline.IsExpired(oneTickBefore),
            "the wrapped deadline must remain live one timestamp unit before its boundary");
        Ensure(RpcDeadline.GetRemaining(deadline.Timestamp, oneTickBefore, frequency) == TimeSpan.FromTicks(1),
            "the final wrapped timestamp unit must remain observable");

        var exactBoundary = unchecked(start + frequency);
        Ensure(deadline.IsExpired(exactBoundary),
            "the wrapped deadline must expire at its exact modular boundary");
        Ensure(RpcDeadline.GetRemaining(deadline.Timestamp, exactBoundary, frequency) == TimeSpan.Zero,
            "remaining time must reach zero at the wrapped boundary");
    }

    [Test]
    public void AddElapsedDurationShouldMatchWrappedDeadlineProjection()
    {
        const long frequency = TimeSpan.TicksPerSecond;
        var start = long.MaxValue - 5_000_000;
        var elapsedTarget = SharpLinkTime.AddElapsedDuration(
            start,
            TimeSpan.FromSeconds(1),
            frequency);
        var deadlineTarget = SharpLinkTime.AddDuration(
            start,
            TimeSpan.FromSeconds(1),
            frequency);

        Ensure(elapsedTarget == deadlineTarget && elapsedTarget < 0,
            "elapsed-duration ordering and deadline projection must cross the signed boundary coherently");
    }

    [Test]
    public void AddDurationShouldRejectAmbiguousMoreThanHalfRingLifetime()
    {
        try
        {
            _ = SharpLinkTime.AddDuration(
                0,
                TimeSpan.FromSeconds(2),
                long.MaxValue);
            throw new Exception("expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
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

    private sealed class FixedTimestampTimeProvider(long timestamp, long frequency) : TimeProvider
    {
        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() => timestamp;
    }
}
