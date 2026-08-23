namespace SharpLink.Abstractions;

/// <summary>
/// Represents a process-local RPC lifetime boundary using a monotonic timestamp.
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
                : SharpLinkTime.AddDuration(timestampNow, remaining, timestampFrequency));
    }

    internal static RpcDeadline Create(TimeSpan timeBudget, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        var utcNow = timeProvider.GetUtcNow();
        var maximum = DateTimeOffset.MaxValue - utcNow;
        var utcDeadline = timeBudget >= maximum ? DateTimeOffset.MaxValue : utcNow.Add(timeBudget);
        var timestampNow = timeProvider.GetTimestamp();
        return new RpcDeadline(
            utcDeadline,
            timeBudget == TimeSpan.Zero
                ? timestampNow
                : SharpLinkTime.AddDuration(timestampNow, timeBudget, timeProvider.TimestampFrequency));
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
               Timestamp <= SharpLinkTime.AddDuration(now, delay, timeProvider.TimestampFrequency);
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
        => SharpLinkTime.GetRemaining(deadlineTimestamp, timestampNow, timestampFrequency);
}
