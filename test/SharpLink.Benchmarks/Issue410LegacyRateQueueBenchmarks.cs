using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class Issue410LegacyRateQueueBenchmarks
{
    private readonly ManualTimeProvider _tokenTime = new();
    private readonly ManualTimeProvider _slidingTime = new();
    private AdmissionRateState _token = null!;
    private AdmissionRateState _sliding = null!;

    [GlobalSetup]
    public void Setup()
    {
        _token = CreateTokenBucket(_tokenTime);
        _sliding = CreateSlidingWindow(_slidingTime);
        Consume(_token);
        Consume(_sliding);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _token.Dispose();
        _sliding.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async ValueTask TokenBucketQueueAndReplenish()
    {
        var pending = _token.AcquireAsync(1, CancellationToken.None);
        _tokenTime.Advance(TimeSpan.FromSeconds(1));
        using var lease = await pending.ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("TokenBucket waiter was not granted after replenishment.");
    }

    [Benchmark]
    public async ValueTask SlidingWindowQueueAndExpire()
    {
        var pending = _sliding.AcquireAsync(1, CancellationToken.None);
        _slidingTime.Advance(TimeSpan.FromSeconds(2));
        using var lease = await pending.ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("SlidingWindow waiter was not granted after expiry.");
    }

    private static AdmissionRateState CreateTokenBucket(TimeProvider timeProvider)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseTokenBucket(options =>
        {
            options.TokenLimit = 1;
            options.TokensPerPeriod = 1;
            options.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        });
        return AdmissionRateState.Create(rule, timeProvider);
    }

    private static AdmissionRateState CreateSlidingWindow(TimeProvider timeProvider)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseSlidingWindow(options =>
        {
            options.PermitLimit = 1;
            options.Window = TimeSpan.FromSeconds(2);
            options.SegmentsPerWindow = 2;
        });
        return AdmissionRateState.Create(rule, timeProvider);
    }

    private static void Consume(RateLimiter limiter)
    {
        using var lease = limiter.AttemptAcquire(1);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Expected setup permit.");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        internal void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            List<ManualTimer> due = [];
            lock (_gate)
            {
                _timestamp = checked(_timestamp + delta.Ticks);
                foreach (var timer in _timers)
                    if (timer.TakeIfDueLocked(_timestamp))
                        due.Add(timer);
            }

            foreach (var timer in due)
                timer.Invoke();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer.IsDisposed)
                    return false;
                timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_timestamp + Math.Max(0, dueTime.Ticks));
                timer.PeriodTicks = period == Timeout.InfiniteTimeSpan
                    ? 0
                    : Math.Max(1, period.Ticks);
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.IsDisposed = true;
                timer.DueTimestamp = long.MaxValue;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            internal long DueTimestamp = long.MaxValue;
            internal long PeriodTicks;
            internal bool IsDisposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.Change(this, dueTime, period);

            internal bool TakeIfDueLocked(long now)
            {
                if (IsDisposed || DueTimestamp > now)
                    return false;
                DueTimestamp = PeriodTicks == 0
                    ? long.MaxValue
                    : checked(DueTimestamp + PeriodTicks);
                return true;
            }

            internal void Invoke() => callback(state);

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
