namespace SharpLink.Server;

/// <summary>
/// Allocation-free, lock-free fixed-window gate that admits at most one log event per
/// interval from a single instance-wide slot. Events that arrive inside an already-admitted
/// window are counted, and the next admitted event reports how many were suppressed.
/// This deliberately carries no per-endpoint, per-session, per-reason, or per-message state:
/// hostile peers must not be able to grow its memory footprint.
/// </summary>
internal struct FixedWindowLogThrottle
{
    private readonly long _intervalTimestampTicks;
    private long _nextLogTimestamp;
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
        // The first event is always admitted.
        _nextLogTimestamp = long.MinValue;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the event at <paramref name="timestamp"/> may be
    /// logged. Suppressed events are counted and reported by the next admitted event.
    /// A saturated boundary keeps the gate closed while the counter stays at the top of its
    /// range; a provider whose timestamp rolls over past <see cref="long.MaxValue"/> (into
    /// the negative half of Int64) reopens the gate, mirroring the rollover-safe elapsed
    /// arithmetic of <see cref="TimeProvider.GetElapsedTime(long, long)"/>.
    /// </summary>
    internal bool ShouldLog(long timestamp, out int suppressedCount)
    {
        while (true)
        {
            var next = Volatile.Read(ref _nextLogTimestamp);
            // A saturated boundary is closed while the counter is still at the top of its
            // range, but a wrapped (negative) timestamp means the provider counter rolled
            // over past long.MaxValue: GetElapsedTime-style arithmetic keeps working, so
            // the gate must reopen. Any other timestamp is judged against the boundary.
            var suppress = next == long.MaxValue
                ? timestamp >= 0
                : timestamp < next;
            if (suppress)
            {
                Interlocked.Increment(ref _suppressedCount);
                suppressedCount = 0;
                return false;
            }

            var newNext = timestamp > long.MaxValue - _intervalTimestampTicks
                ? long.MaxValue
                : timestamp + _intervalTimestampTicks;
            if (Interlocked.CompareExchange(ref _nextLogTimestamp, newNext, next) != next)
                continue;

            suppressedCount = Interlocked.Exchange(ref _suppressedCount, 0);
            return true;
        }
    }
}
