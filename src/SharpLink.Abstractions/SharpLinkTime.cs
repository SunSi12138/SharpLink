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

        var delta = duration.TotalSeconds * timestampFrequency;
        if (delta >= long.MaxValue)
            return long.MaxValue;
        var timestampDelta = Math.Max(1L, (long)Math.Ceiling(delta));
        return timestamp > long.MaxValue - timestampDelta
            ? long.MaxValue
            : timestamp + timestampDelta;
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        // TimeProvider timestamps may occupy the full Int64 range. Perform the
        // subtraction after widening so an extreme but valid pair cannot wrap.
        var remaining = (double)deadlineTimestamp - timestampNow;
        if (remaining <= 0)
            return TimeSpan.Zero;
        var ticks = remaining * TimeSpan.TicksPerSecond / timestampFrequency;
        if (ticks >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(Math.Max(1L, (long)Math.Ceiling(ticks)));
    }
}
