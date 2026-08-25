namespace SharpLink.Abstractions;

/// <summary>
/// Represents a process-local RPC lifetime boundary using a monotonic timestamp origin and budget.
/// </summary>
internal readonly struct RpcDeadline
{
    private readonly long _timestampOrigin;
    private readonly long _timestampFrequency;
    private readonly TimeSpan _timeBudget;
    private readonly bool _usesTimeBudget;

    private RpcDeadline(long timestamp)
    {
        Timestamp = timestamp;
        HasValue = true;
        _timestampOrigin = 0;
        _timestampFrequency = 0;
        _timeBudget = default;
        _usesTimeBudget = false;
    }

    private RpcDeadline(
        long timestamp,
        long timestampOrigin,
        long timestampFrequency,
        TimeSpan timeBudget)
    {
        Timestamp = timestamp;
        HasValue = true;
        _timestampOrigin = timestampOrigin;
        _timestampFrequency = timestampFrequency;
        _timeBudget = timeBudget;
        _usesTimeBudget = true;
    }

    internal bool HasValue { get; }

    /// <summary>
    /// Saturating projection retained for diagnostics and legacy internal tests. Expiry and ordering
    /// for deadlines created from a TimeBudget never depend on this signed absolute value.
    /// </summary>
    internal long Timestamp { get; }

    internal static RpcDeadline Create(TimeSpan timeBudget, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        var timestampNow = timeProvider.GetTimestamp();
        return Create(timeBudget, timestampNow, timeProvider.TimestampFrequency);
    }

    internal static RpcDeadline Create(
        TimeSpan timeBudget,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timestampFrequency, 0);
        var timestamp = timeBudget == TimeSpan.Zero
            ? timestampNow
            : SharpLinkTime.AddDuration(timestampNow, timeBudget, timestampFrequency);
        return new RpcDeadline(timestamp, timestampNow, timestampFrequency, timeBudget);
    }

    internal static RpcDeadline FromTimestamp(long timestamp)
        => new(timestamp);

    internal bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!HasValue)
            return false;
        return _usesTimeBudget
            ? GetBudgetRemaining(timeProvider.GetTimestamp()) <= TimeSpan.Zero
            : Timestamp <= timeProvider.GetTimestamp();
    }

    internal bool IsExpired(long timestamp)
    {
        if (!HasValue)
            return false;
        return _usesTimeBudget
            ? GetBudgetRemaining(timestamp) <= TimeSpan.Zero
            : Timestamp <= timestamp;
    }

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
        if (_usesTimeBudget && other._usesTimeBudget)
            return GetBudgetRemaining(timestampNow) <= other.GetBudgetRemaining(timestampNow);
        return Timestamp <= other.Timestamp;
    }

    internal TimeSpan GetRemaining(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!HasValue)
            return TimeSpan.MaxValue;
        return _usesTimeBudget
            ? GetBudgetRemaining(timeProvider.GetTimestamp())
            : GetRemaining(Timestamp, timeProvider.GetTimestamp(), timeProvider.TimestampFrequency);
    }

    private TimeSpan GetBudgetRemaining(long timestampNow)
    {
        var elapsed = SharpLinkTime.GetElapsed(
            _timestampOrigin,
            timestampNow,
            _timestampFrequency);
        if (elapsed >= _timeBudget)
            return TimeSpan.Zero;
        return _timeBudget - elapsed;
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
        => SharpLinkTime.GetRemaining(deadlineTimestamp, timestampNow, timestampFrequency);
}
