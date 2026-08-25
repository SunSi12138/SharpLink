namespace SharpLink.Abstractions;

/// <summary>Provides overflow-safe arithmetic for instance-owned monotonic clocks.</summary>
internal static class SharpLinkTime
{
    internal static long AddDuration(
        long timestamp,
        TimeSpan duration,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        if (duration == TimeSpan.Zero)
            return timestamp;

        var timestampDelta = GetTimestampDelta(duration, timestampFrequency, roundUp: true);
        return unchecked(timestamp + timestampDelta);
    }

    internal static long AddElapsedDuration(
        long timestamp,
        TimeSpan duration,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        if (duration == TimeSpan.Zero)
            return timestamp;

        var timestampDelta = GetTimestampDelta(duration, timestampFrequency, roundUp: false);
        return unchecked(timestamp + timestampDelta);
    }

    internal static bool IsReached(long deadlineTimestamp, long timestampNow)
        => unchecked(timestampNow - deadlineTimestamp) >= 0;

    internal static bool IsEarlierOrEqual(
        long candidateDeadlineTimestamp,
        long currentDeadlineTimestamp,
        long timestampNow)
    {
        var candidateRemaining = GetRemainingTimestampUnits(candidateDeadlineTimestamp, timestampNow);
        var currentRemaining = GetRemainingTimestampUnits(currentDeadlineTimestamp, timestampNow);
        return candidateRemaining <= currentRemaining;
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        var remainingTimestampUnits = GetRemainingTimestampUnits(deadlineTimestamp, timestampNow);
        if (remainingTimestampUnits == 0)
            return TimeSpan.Zero;

        var numerator = (UInt128)(ulong)remainingTimestampUnits * (uint)TimeSpan.TicksPerSecond;
        var denominator = (UInt128)(ulong)timestampFrequency;
        var ticks = (numerator + denominator - 1) / denominator;
        if (ticks >= (UInt128)TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks((long)ticks);
    }

    private static long GetRemainingTimestampUnits(long deadlineTimestamp, long timestampNow)
    {
        var remaining = unchecked(deadlineTimestamp - timestampNow);
        return remaining > 0 ? remaining : 0;
    }

    private static long GetTimestampDelta(
        TimeSpan duration,
        long timestampFrequency,
        bool roundUp)
    {
        var numerator = (UInt128)(ulong)duration.Ticks * (ulong)timestampFrequency;
        var denominator = (UInt128)TimeSpan.TicksPerSecond;
        var timestampDelta = roundUp
            ? (numerator + denominator - 1) / denominator
            : numerator / denominator;
        if (roundUp && timestampDelta == 0)
            timestampDelta = 1;
        if (timestampDelta > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "The duration exceeds the unambiguous range of the monotonic timestamp counter.");
        }
        return (long)timestampDelta;
    }
}
