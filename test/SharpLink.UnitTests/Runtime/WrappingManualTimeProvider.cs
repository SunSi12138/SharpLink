using System.Collections.Generic;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Deterministic test clock whose timestamp counter intentionally wraps across the signed Int64
/// boundary. Timer scheduling is tracked as relative delay, matching TimeProvider's contract
/// without imposing signed ordering on absolute timestamp values.
/// </summary>
internal sealed class WrappingManualTimeProvider(long initialTimestamp) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<WrappingTimer> _timers = [];
    private long _timestamp = initialTimestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _timestamp;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new WrappingTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        var remaining = elapsed.Ticks;

        while (true)
        {
            TimerCallback? callback = null;
            object? state = null;
            lock (_gate)
            {
                var next = FindNextTimer(remaining);
                if (next is null)
                {
                    MoveClockAndTimers(remaining);
                    return;
                }

                var delta = next.RemainingTicks;
                MoveClockAndTimers(delta);
                remaining -= delta;
                next.PrepareNextTick();
                callback = next.Callback;
                state = next.State;
            }

            callback(state);
        }
    }

    private WrappingTimer? FindNextTimer(long maximumDelay)
    {
        WrappingTimer? next = null;
        for (var index = 0; index < _timers.Count; index++)
        {
            var candidate = _timers[index];
            if (candidate.IsDisposed || candidate.RemainingTicks == long.MaxValue ||
                candidate.RemainingTicks > maximumDelay)
            {
                continue;
            }

            if (next is null || candidate.RemainingTicks < next.RemainingTicks)
                next = candidate;
        }
        return next;
    }

    private void MoveClockAndTimers(long elapsedTicks)
    {
        _timestamp = unchecked(_timestamp + elapsedTicks);
        if (elapsedTicks == 0)
            return;

        for (var index = 0; index < _timers.Count; index++)
        {
            var timer = _timers[index];
            if (timer.IsDisposed || timer.RemainingTicks == long.MaxValue)
                continue;
            timer.RemainingTicks -= elapsedTicks;
        }
    }

    private bool ChangeTimer(WrappingTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        var dueTicks = ValidateDelay(dueTime, nameof(dueTime));
        var periodTicks = ValidateDelay(period, nameof(period));
        lock (_gate)
        {
            if (timer.IsDisposed)
                return false;
            if (!_timers.Contains(timer))
                _timers.Add(timer);
            timer.RemainingTicks = dueTicks;
            timer.PeriodTicks = periodTicks <= 0 ? long.MaxValue : periodTicks;
            return true;
        }
    }

    private void DisposeTimer(WrappingTimer timer)
    {
        lock (_gate)
        {
            if (timer.IsDisposed)
                return;
            timer.IsDisposed = true;
            timer.RemainingTicks = long.MaxValue;
            _timers.Remove(timer);
        }
    }

    private static long ValidateDelay(TimeSpan value, string parameterName)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return long.MaxValue;
        if (value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName);
        return value.Ticks;
    }

    private sealed class WrappingTimer(
        WrappingManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long RemainingTicks { get; set; } = long.MaxValue;
        internal long PeriodTicks { get; set; } = long.MaxValue;
        internal bool IsDisposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => owner.ChangeTimer(this, dueTime, period);

        public void Dispose() => owner.DisposeTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void PrepareNextTick()
            => RemainingTicks = PeriodTicks;
    }
}
