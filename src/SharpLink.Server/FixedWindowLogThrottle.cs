namespace SharpLink.Server;

/// <summary>
/// Allocation-free, lock-free fixed-window gate that admits at most one log event per
/// interval from a single instance-wide slot. Events that arrive inside an already-admitted
/// window are counted, and the next admitted event reports how many were suppressed.
/// Admission uses unchecked timestamp subtraction, which keeps the window exact across
/// Int64 counter rollover for intervals below half the timestamp range — the same
/// rollover-safe elapsed arithmetic as <see cref="TimeProvider.GetElapsedTime(long, long)"/>.
/// This deliberately carries no per-endpoint, per-session, per-reason, or per-message
/// state: hostile peers must not be able to grow its memory footprint.
/// </summary>
internal struct FixedWindowLogThrottle
{
    private readonly long _intervalTimestampTicks;
    private long _lastAdmittedTimestamp;
    private int _initialized;
    private int _suppressedCount;

    internal FixedWindowLogThrottle(TimeSpan interval, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        // Convert the interval into the time provider's timestamp unit. Ticks are already
        // provider-independent; the frequency scales them into timestamp ticks. Only the
        // FINAL result saturates: a high-but-valid frequency (e.g. 1e12 Hz) must keep its
        // exact five-second window even when the intermediate frequency*ticks product
        // overflows Int64. This ctor runs once per server instance, so decimal arithmetic
        // is fine here; the per-event path below stays pure integer/Interlocked.
        var intervalTicks = interval.Ticks;
        if (intervalTicks == 0)
        {
            _intervalTimestampTicks = 0;
        }
        else if (timestampFrequency > decimal.MaxValue / intervalTicks)
        {
            _intervalTimestampTicks = long.MaxValue;
        }
        else
        {
            var scaled = (decimal)timestampFrequency * intervalTicks / TimeSpan.TicksPerSecond;
            _intervalTimestampTicks = scaled >= long.MaxValue
                ? long.MaxValue
                : Math.Max(1, (long)scaled);
        }
        _lastAdmittedTimestamp = 0;
        _initialized = 0;
        _suppressedCount = 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the event at <paramref name="timestamp"/> may be
    /// logged. Suppressed events are counted and reported by the next admitted event.
    /// A provider whose counter stays at the top of its range, or wraps past
    /// <see cref="long.MaxValue"/>, keeps the same fixed-window semantics: a window opened
    /// shortly before rollover stays closed for its full remaining interval.
    /// </summary>
    internal bool ShouldLog(long timestamp, out int suppressedCount)
    {
        while (true)
        {
            var last = Volatile.Read(ref _lastAdmittedTimestamp);
            if (Volatile.Read(ref _initialized) != 0 &&
                unchecked(timestamp - last) < _intervalTimestampTicks)
            {
                Interlocked.Increment(ref _suppressedCount);
                suppressedCount = 0;
                return false;
            }

            // Publish initialization before the admitted timestamp so a racing caller that
            // observes the flag together with the stale timestamp gets the conservative
            // suppressed outcome and retries against the committed value.
            Volatile.Write(ref _initialized, 1);
            if (Interlocked.CompareExchange(ref _lastAdmittedTimestamp, timestamp, last) != last)
                continue;

            suppressedCount = Interlocked.Exchange(ref _suppressedCount, 0);
            return true;
        }
    }
}
