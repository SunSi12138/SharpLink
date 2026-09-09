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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static TimeSpan GetElapsed(long timestampOrigin, long timestampNow, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        var units = unchecked((ulong)(timestampNow - timestampOrigin));
        if (units <= ulong.MaxValue / (uint)TimeSpan.TicksPerSecond)
        {
            var ticks = units * (uint)TimeSpan.TicksPerSecond / (ulong)timestampFrequency;
            return TimeSpan.FromTicks((long)Math.Min(ticks, (ulong)long.MaxValue));
        }
        return GetElapsedWide(units, timestampFrequency);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static TimeSpan GetElapsedWide(ulong units, long timestampFrequency)
    {
        var ticks = (UInt128)units * (uint)TimeSpan.TicksPerSecond / (ulong)timestampFrequency;
        return ticks >= (UInt128)long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
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
