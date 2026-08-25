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
        var provider = new MutableTimestampTimeProvider(timestamp: 0, frequency: 1);
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
    public void RpcDeadlineShouldPreserveFullLifetimeAcrossSignedTimestampBoundary()
    {
        const long frequency = TimeSpan.TicksPerSecond;
        var start = long.MaxValue - 5_000_000;
        var provider = new MutableTimestampTimeProvider(start, frequency);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), provider);

        Ensure(deadline.GetRemaining(provider) == TimeSpan.FromSeconds(1),
            "crossing the signed timestamp boundary must preserve the full configured lifetime");

        provider.Timestamp = unchecked(start + frequency - 1);
        Ensure(!deadline.IsExpired(provider),
            "the deadline must remain live one timestamp unit before its modular boundary");
        Ensure(deadline.GetRemaining(provider) == TimeSpan.FromTicks(1),
            "the final timestamp unit must remain observable after crossing the signed boundary");

        provider.Timestamp = unchecked(start + frequency);
        Ensure(deadline.IsExpired(provider),
            "the deadline must expire at its exact modular boundary");
        Ensure(deadline.GetRemaining(provider) == TimeSpan.Zero,
            "remaining time must reach zero at the modular boundary");
    }

    [Test]
    public void RpcDeadlineShouldAcceptLifetimeBeyondSignedHalfRing()
    {
        const long frequency = long.MaxValue;
        var provider = new MutableTimestampTimeProvider(timestamp: 0, frequency);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(2), provider);

        Ensure(deadline.GetRemaining(provider) == TimeSpan.FromSeconds(2),
            "a large positive lifetime must not be rejected because it exceeds the signed half-ring");

        provider.Timestamp = long.MaxValue;
        Ensure(deadline.GetRemaining(provider) == TimeSpan.FromSeconds(1),
            "one second of modular elapsed time must deduct exactly one second");

        provider.Timestamp = -2;
        Ensure(deadline.IsExpired(provider),
            "a lifetime spanning almost the full 64-bit counter ring must expire at its modular boundary");
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

    private sealed class MutableTimestampTimeProvider(long timestamp, long frequency) : TimeProvider
    {
        internal long Timestamp { get; set; } = timestamp;

        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() => Timestamp;
    }
}
