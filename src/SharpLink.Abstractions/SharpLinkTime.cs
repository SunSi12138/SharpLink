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

        var numerator = (UInt128)(ulong)duration.Ticks * (ulong)timestampFrequency;
        var denominator = (UInt128)TimeSpan.TicksPerSecond;
        var timestampDelta = (numerator + denominator - 1) / denominator;
        if (timestampDelta == 0)
            timestampDelta = 1;
        var result = (Int128)timestamp + (Int128)timestampDelta;
        return result >= long.MaxValue
            ? long.MaxValue
            : (long)result;
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

        var numerator = (UInt128)(ulong)duration.Ticks * (ulong)timestampFrequency;
        var timestampDelta = numerator / (UInt128)TimeSpan.TicksPerSecond;
        var result = (Int128)timestamp + (Int128)timestampDelta;
        return result >= long.MaxValue
            ? long.MaxValue
            : (long)result;
    }

    internal static TimeSpan GetElapsed(
        long timestampOrigin,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);

        // TimeProvider timestamps are monotonic counter values, not signed wall-clock values.
        // Interpret subtraction modulo 2^64 so crossing Int64.MaxValue -> Int64.MinValue does not
        // manufacture a negative elapsed duration. A single 64-bit counter cannot distinguish
        // multiple complete wraps; treating the observable modular interval as elapsed is
        // conservative for RPC lifetimes because it can delay, but never advance, expiry.
        var elapsedTimestampUnits = unchecked((ulong)(timestampNow - timestampOrigin));
        if (elapsedTimestampUnits == 0)
            return TimeSpan.Zero;

        var numerator = (UInt128)elapsedTimestampUnits * (uint)TimeSpan.TicksPerSecond;
        var ticks = numerator / (UInt128)(ulong)timestampFrequency;
        if (ticks >= (UInt128)TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks((long)ticks);
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        var remainingTimestampUnits = (Int128)deadlineTimestamp - timestampNow;
        if (remainingTimestampUnits <= 0)
            return TimeSpan.Zero;

        var numerator = (UInt128)remainingTimestampUnits * (uint)TimeSpan.TicksPerSecond;
        var denominator = (UInt128)(ulong)timestampFrequency;
        var ticks = (numerator + denominator - 1) / denominator;
        if (ticks >= (UInt128)TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks((long)ticks);
    }
}
