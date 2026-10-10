namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterReadyPumpBridgeTests
{
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private long _ticks = 1;
        internal readonly TaskCompletionSource TimerArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            List<ManualTimer> fire = new();
            lock (_gate)
            {
                _ticks = checked(_ticks + duration.Ticks);
                foreach (var timer in _timers)
                {
                    if (timer.Disposed || timer.Due > _ticks)
                        continue;
                    timer.Due = timer.Period > 0 ? checked(_ticks + timer.Period) : long.MaxValue;
                    fire.Add(timer);
                }
            }
            foreach (var timer in fire)
                timer.Callback(timer.State);
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = long.MaxValue;
            internal long Period;
            internal bool Disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Disposed)
                        return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock._ticks + dueTime.Ticks);
                    Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                    if (Due != long.MaxValue)
                        clock.TimerArmed.TrySetResult();
                    return true;
                }
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    Disposed = true;
                    Due = long.MaxValue;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
