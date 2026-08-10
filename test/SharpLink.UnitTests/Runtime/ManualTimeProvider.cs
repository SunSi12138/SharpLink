using System.Collections.Generic;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Deterministic, monotonic test clock for lifecycle tests. Production code is not coupled to
/// this helper; later TimeProvider migrations can inject it without sleeping on wall-clock time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset DefaultStart =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _timersDrained = CreateCompletedSignal();
    private DateTimeOffset _utcNow;
    private long _timestamp;
    private int _utcNowReadCount;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _utcNow = start ?? DefaultStart;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            _utcNowReadCount++;
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
            return _timestamp;
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
                return _timers.Count;
        }
    }

    internal long EarliestTimerTimestamp
    {
        get
        {
            lock (_gate)
            {
                var earliest = long.MaxValue;
                for (var index = 0; index < _timers.Count; index++)
                {
                    var timer = _timers[index];
                    if (!timer.IsDisposed && timer.NextTimestamp < earliest)
                        earliest = timer.NextTimestamp;
                }
                return earliest;
            }
        }
    }

    internal Task WaitForTimersDrainedAsync()
    {
        lock (_gate)
            return _timersDrained.Task;
    }

    public int UtcNowReadCount
    {
        get
        {
            lock (_gate)
                return _utcNowReadCount;
        }
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_gate)
            _utcNow = utcNow;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        long target;
        lock (_gate)
            target = SaturatingAdd(_timestamp, elapsed.Ticks);

        while (true)
        {
            TimerCallback callback;
            object? state;
            lock (_gate)
            {
                var nextTimer = FindNextTimer(target);
                if (nextTimer is null)
                {
                    MoveClock(target);
                    return;
                }

                MoveClock(nextTimer.NextTimestamp);
                nextTimer.PrepareNextTick();
                callback = nextTimer.Callback;
                state = nextTimer.State;
            }

            callback(state);
        }
    }

    private ManualTimer? FindNextTimer(long target)
    {
        ManualTimer? next = null;
        for (var index = 0; index < _timers.Count; index++)
        {
            var candidate = _timers[index];
            if (candidate.IsDisposed || candidate.NextTimestamp > target)
                continue;
            if (next is null || candidate.NextTimestamp < next.NextTimestamp)
                next = candidate;
        }
        return next;
    }

    private void MoveClock(long timestamp)
    {
        var delta = timestamp - _timestamp;
        _timestamp = timestamp;
        _utcNow = _utcNow.AddTicks(delta);
    }

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        var dueTicks = ValidateDelay(dueTime, nameof(dueTime));
        var periodTicks = ValidateDelay(period, nameof(period));

        lock (_gate)
        {
            if (timer.IsDisposed)
                return false;
            if (!_timers.Contains(timer))
            {
                if (_timers.Count == 0)
                {
                    _timersDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _timers.Add(timer);
            }

            timer.PeriodTicks = periodTicks <= 0 ? long.MaxValue : periodTicks;
            timer.NextTimestamp = dueTicks == long.MaxValue
                ? long.MaxValue
                : SaturatingAdd(_timestamp, dueTicks);
            return true;
        }
    }

    private void DisposeTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            if (timer.IsDisposed)
                return;
            timer.IsDisposed = true;
            timer.NextTimestamp = long.MaxValue;
            _timers.Remove(timer);
            if (_timers.Count == 0)
                _timersDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
        return signal;
    }

    private static long ValidateDelay(TimeSpan value, string parameterName)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return long.MaxValue;
        if (value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName);
        return value.Ticks;
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long NextTimestamp { get; set; } = long.MaxValue;
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
        {
            NextTimestamp = PeriodTicks == long.MaxValue
                ? long.MaxValue
                : SaturatingAdd(NextTimestamp, PeriodTicks);
        }
    }
}
