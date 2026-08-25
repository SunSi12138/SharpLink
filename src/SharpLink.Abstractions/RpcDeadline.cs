namespace SharpLink.Abstractions;

/// <summary>
/// Represents a process-local RPC lifetime boundary using a monotonic timestamp.
/// </summary>
internal readonly struct RpcDeadline
{
    private RpcDeadline(long timestamp)
    {
        Timestamp = timestamp;
        HasValue = true;
    }

    internal bool HasValue { get; }

    internal long Timestamp { get; }

    internal static RpcDeadline Create(TimeSpan timeBudget, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        var timestampNow = timeProvider.GetTimestamp();
        return new RpcDeadline(
            timeBudget == TimeSpan.Zero
                ? timestampNow
                : SharpLinkTime.AddDuration(timestampNow, timeBudget, timeProvider.TimestampFrequency));
    }

    internal static RpcDeadline Create(
        TimeSpan timeBudget,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timestampFrequency, 0);
        return new RpcDeadline(
            timeBudget == TimeSpan.Zero
                ? timestampNow
                : SharpLinkTime.AddDuration(timestampNow, timeBudget, timestampFrequency));
    }

    internal static RpcDeadline FromTimestamp(long timestamp)
        => new(timestamp);

    internal bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return HasValue && SharpLinkTime.IsReached(Timestamp, timeProvider.GetTimestamp());
    }

    internal bool IsExpired(long timestamp)
        => HasValue && SharpLinkTime.IsReached(Timestamp, timestamp);

    internal bool WouldExpireBeforeOrAt(
        TimeSpan delay,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (!HasValue)
            return false;
        return GetRemaining(timeProvider) <= delay;
    }

    internal bool IsEarlierOrEqual(RpcDeadline other, long timestampNow)
    {
        if (!HasValue)
            return false;
        if (!other.HasValue)
            return true;
        return SharpLinkTime.IsEarlierOrEqual(Timestamp, other.Timestamp, timestampNow);
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
