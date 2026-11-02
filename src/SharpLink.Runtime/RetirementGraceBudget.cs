namespace SharpLink.Runtime;

// Cold lifecycle state only. An elected retirement carries the winning public
// call's original sample; neither a later waiter nor a timer rearm recaptures it.
internal readonly struct RetirementGraceBudget
{
    private readonly TimeProvider? _provider;

    private RetirementGraceBudget(TimeProvider provider, long frequency, long origin, TimeSpan duration)
    {
        _provider = provider;
        TimestampFrequency = frequency;
        TimestampOrigin = origin;
        Duration = duration;
    }

    internal TimeProvider Provider => _provider ??
        throw new InvalidOperationException("A retirement budget must be captured before election.");
    internal long TimestampFrequency { get; }
    internal long TimestampOrigin { get; }
    internal TimeSpan Duration { get; }

    internal static RetirementGraceBudget Capture(TimeSpan duration, TimeProvider provider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(provider);
        // These are user-provider calls, made before election and outside host gates.
        // Failure here does not authorize retirement of the still-running module.
        var frequency = provider.TimestampFrequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return new RetirementGraceBudget(provider, frequency, provider.GetTimestamp(), duration);
    }

    internal RetirementGraceClock CreateClock() => new(this);
}

// One mutable copy belongs to the cold cancellation driver. This is not RpcDeadline:
// full TimeSpan budgets are retained, with no RPC half-ring duration normalization.
internal struct RetirementGraceClock
{
    private const ulong TicksPerSecond = TimeSpan.TicksPerSecond;
    private const ulong AmbiguousDelta = 1UL << 63;
    internal const ulong MaximumScheduledTimestampUnits = (1UL << 62) - 1;

    private readonly TimeProvider _provider;
    private readonly ulong _frequency;
    private readonly long _maximumSliceTicks;
    private long _acceptedTimestamp;
    private UInt128 _remainingNumerator;

    internal RetirementGraceClock(RetirementGraceBudget budget)
    {
        _provider = budget.Provider;
        _frequency = (ulong)budget.TimestampFrequency;
        _acceptedTimestamp = budget.TimestampOrigin;
        // b,f <= long.MaxValue => b*f < 2^126. Keep the rational remainder;
        // rounding every elapsed sample would lose short-interval consumption.
        _remainingNumerator = (UInt128)(ulong)budget.Duration.Ticks * _frequency;
        var windowTicks = (UInt128)MaximumScheduledTimestampUnits * TicksPerSecond / _frequency;
        var nativeTicks = (ulong)SharpLinkTimer.MaximumDelay.Ticks;
        _maximumSliceTicks = (long)(windowTicks < nativeTicks ? windowTicks : nativeTicks);
    }

    internal bool IsExpired => _remainingNumerator == 0;
    internal long AcceptedTimestamp => _acceptedTimestamp;
    internal UInt128 RemainingNumerator => _remainingNumerator;

    internal TimeSpan Remaining
    {
        get
        {
            // ceil(R/f), not ceil(elapsed). R+f-1 remains below 2^127.
            var ticks = (_remainingNumerator + _frequency - 1) / _frequency;
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    internal TimeSpan NextSlice
        => TimeSpan.FromTicks(Math.Min(Remaining.Ticks, _maximumSliceTicks));

    internal bool Observe() => ObserveTimestamp(_provider.GetTimestamp());

    internal bool ObserveTimestamp(long timestamp)
    {
        var delta = unchecked((ulong)(timestamp - _acceptedTimestamp));
        if (delta >= AmbiguousDelta)
        {
            // Backward/ambiguous samples consume nothing AND leave the accepted
            // baseline alone. Moving it backward would charge the recovery twice.
            return false;
        }

        var elapsed = (UInt128)delta * TicksPerSecond; // < 2^87
        _remainingNumerator = elapsed >= _remainingNumerator ? 0 : _remainingNumerator - elapsed;
        _acceptedTimestamp = timestamp;
        return true;
    }

    // The scheduled slice guarantees ceil(sliceTicks*f/T) <= 2^62-1, NOT that
    // a thread runs or a synchronous provider returns inside that window. A whole
    // unobserved timestamp ring (including slow prepublication preparation) is
    // unrecoverable. A thrown provider error and a blocked provider are distinct.
}
