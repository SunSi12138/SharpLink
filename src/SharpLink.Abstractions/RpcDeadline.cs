namespace SharpLink.Abstractions;

/// <summary>
/// Represents a process-local RPC lifetime boundary using a monotonic timestamp origin and budget.
/// </summary>
internal readonly struct RpcDeadline
{
    private static readonly UInt128 TimestampHalfRing = (UInt128)1 << 63;

    private readonly long _timestampOrigin;
    private readonly long _timestampFrequency;
    private readonly TimeSpan _timeBudget;
    private readonly bool _usesTimeBudget;

    private RpcDeadline(long timestamp)
    {
        _timestampUnits = unchecked((ulong)timestamp);
        HasValue = true;
        _timestampOrigin = 0;
        _timestampFrequency = 0;
        _timeBudget = default;
        _usesTimeBudget = false;
    }

    private RpcDeadline(
        ulong timestampUnits,
        long timestampOrigin,
        long timestampFrequency,
        TimeSpan timeBudget)
    {
        _timestampUnits = timestampUnits;
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
    private readonly ulong _timestampUnits;

    internal long Timestamp
    {
        get
        {
            if (!_usesTimeBudget)
                return unchecked((long)_timestampUnits);
            var projected = (Int128)_timestampOrigin + _timestampUnits;
            return projected >= long.MaxValue ? long.MaxValue : (long)projected;
        }
    }

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
        timeBudget = NormalizeFiniteBudget(timeBudget, timestampFrequency);
        // NormalizeFiniteBudget guarantees that this exact ceiling fits inside half a ring.
        var numerator = (UInt128)(ulong)timeBudget.Ticks * (ulong)timestampFrequency;
        var timestampUnits = (ulong)((numerator + (uint)TimeSpan.TicksPerSecond - 1) /
            (uint)TimeSpan.TicksPerSecond);
        return new RpcDeadline(timestampUnits, timestampNow, timestampFrequency, timeBudget);
    }

    internal static RpcDeadline FromTimestamp(long timestamp)
        => new(timestamp);

    internal bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!HasValue)
            return false;
        return _usesTimeBudget
            ? unchecked((ulong)(timeProvider.GetTimestamp() - _timestampOrigin)) >= _timestampUnits
            : Timestamp <= timeProvider.GetTimestamp();
    }

    internal bool IsExpired(long timestamp)
    {
        if (!HasValue)
            return false;
        return _usesTimeBudget
            ? unchecked((ulong)(timestamp - _timestampOrigin)) >= _timestampUnits
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

    internal TimeSpan GetRemaining(long timestampNow, long timestampFrequency)
    {
        if (!HasValue)
            return TimeSpan.MaxValue;
        return _usesTimeBudget
            ? GetBudgetRemaining(timestampNow)
            : GetRemaining(Timestamp, timestampNow, timestampFrequency);
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

    private static TimeSpan NormalizeFiniteBudget(TimeSpan timeBudget, long timestampFrequency)
    {
        if (timeBudget == TimeSpan.Zero)
            return timeBudget;

        // A 64-bit timestamp alone cannot recover how many complete counter rings elapsed between
        // observations. Use the standard modular-clock contract: every ordinary finite RPC
        // lifetime must fit strictly inside one half ring, where elapsed/ordering remains
        // unambiguous. TimeSpan.MaxValue is the existing public "far future" saturation value;
        // preserve that API contract by saturating it to the largest unambiguous budget for the
        // supplied provider instead of rejecting it. On normal Stopwatch-backed providers that
        // remains many millennia; pathological high-frequency providers still cannot create a
        // multi-ring finite deadline.
        var numerator = (UInt128)(ulong)timeBudget.Ticks * (ulong)timestampFrequency;
        var denominator = (UInt128)TimeSpan.TicksPerSecond;
        var timestampDelta = (numerator + denominator - 1) / denominator;
        if (timestampDelta < TimestampHalfRing)
            return timeBudget;

        if (timeBudget == TimeSpan.MaxValue)
        {
            var maximumTicks = ((TimestampHalfRing - 1) * denominator) /
                (ulong)timestampFrequency;
            if (maximumTicks == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeBudget),
                    "The TimeProvider timestamp frequency cannot represent a positive finite RPC lifetime.");
            }
            return TimeSpan.FromTicks((long)maximumTicks);
        }

        throw new ArgumentOutOfRangeException(
            nameof(timeBudget),
            "The finite RPC lifetime must fit within half of the TimeProvider timestamp counter ring.");
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
        => SharpLinkTime.GetRemaining(deadlineTimestamp, timestampNow, timestampFrequency);
}
