namespace SharpLink.Abstractions;

/// <summary>
/// Keeps the wire UTC deadline separate from the monotonic timestamp used for local timing.
/// </summary>
internal readonly struct RpcDeadline
{
    private RpcDeadline(DateTimeOffset utcDeadline, long timestamp)
    {
        UtcDeadline = utcDeadline;
        Timestamp = timestamp;
        HasValue = true;
    }

    internal bool HasValue { get; }

    internal DateTimeOffset? UtcDeadline { get; }

    internal long Timestamp { get; }

    internal static RpcDeadline Create(
        DateTimeOffset utcDeadline,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Create(
            utcDeadline,
            timeProvider.GetUtcNow(),
            timeProvider.GetTimestamp(),
            timeProvider.TimestampFrequency);
    }

    internal static RpcDeadline Create(
        DateTimeOffset utcDeadline,
        DateTimeOffset utcNow,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        var remaining = utcDeadline - utcNow;
        return new RpcDeadline(
            utcDeadline,
            remaining <= TimeSpan.Zero
                ? timestampNow
                : AddDuration(timestampNow, remaining, timestampFrequency));
    }

    internal static RpcDeadline Create(DateTimeOffset utcDeadline, long timestamp)
        => new(utcDeadline, timestamp);

    internal bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return HasValue && Timestamp <= timeProvider.GetTimestamp();
    }

    internal bool IsExpired(long timestamp)
        => HasValue && Timestamp <= timestamp;

    internal bool WouldExpireBeforeOrAt(
        TimeSpan delay,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (!HasValue)
            return false;
        var now = timeProvider.GetTimestamp();
        return Timestamp <= now ||
               Timestamp <= AddDuration(now, delay, timeProvider.TimestampFrequency);
    }

    internal TimeSpan GetRemaining(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return GetRemaining(Timestamp, timeProvider.GetTimestamp(), timeProvider.TimestampFrequency);
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

    private static long AddDuration(
        long timestamp,
        TimeSpan duration,
        long timestampFrequency)
    {
        var delta = duration.TotalSeconds * timestampFrequency;
        if (delta >= long.MaxValue)
            return long.MaxValue;
        var timestampDelta = Math.Max(1L, (long)Math.Ceiling(delta));
        return timestamp > long.MaxValue - timestampDelta
            ? long.MaxValue
            : timestamp + timestampDelta;
    }
}
