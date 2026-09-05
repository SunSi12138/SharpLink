using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class Issue410RateQueueBenchmarks
{
    private SharpLinkAdmissionController _controller = null!;
    private ManualTimeProvider _time = null!;
    private SharpLinkAdmissionContext _context = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _time = new ManualTimeProvider();
        _context = new SharpLinkAdmissionContext(1, 2, RpcMethodKind.Unary, "issue410-rate-queue", null, null);
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });
        options.MaxQueuedCalls = 1;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
        _controller = SharpLinkAdmissionController.Create(options, [], _time);
        var consumed = await _controller.AcquireAsync(_context, 1, false, CancellationToken.None);
        if (!consumed.IsAcquired)
            throw new InvalidOperationException("failed to consume initial token");
        consumed.Lease!.Dispose();
    }

    [Benchmark]
    public async ValueTask QueueRateRequestAndRelease()
    {
        var pending = _controller.AcquireAsync(_context, 1, true, CancellationToken.None).AsTask();
        if (pending.IsCompleted)
            throw new InvalidOperationException("rate request did not enter the waiter path");
        _time.Advance(TimeSpan.FromSeconds(10));
        var decision = await pending.ConfigureAwait(false);
        if (!decision.IsAcquired)
            throw new InvalidOperationException("queued rate request was not granted");
        decision.Lease!.Dispose();
    }

    [GlobalCleanup]
    public ValueTask Cleanup() => _controller.DisposeAsync();

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            var now = Interlocked.Add(ref _timestamp, delta.Ticks);
            ManualTimer[] timers;
            lock (_gate)
                timers = [.. _timers];
            foreach (var timer in timers)
                timer.FireIfDue(now);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;
                var due = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner.GetTimestamp() + Math.Max(1, dueTime.Ticks);
                Volatile.Write(ref _due, due);
                return true;
            }

            public void FireIfDue(long now)
            {
                if (Volatile.Read(ref _disposed) != 0 || now < Volatile.Read(ref _due))
                    return;
                Volatile.Write(ref _due, long.MaxValue);
                callback(state);
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class Issue410CompositeBenchmarks
{
    private SharpLinkAdmissionController _controller = null!;
    private SharpLinkAdmissionContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkAdmissionContext(1, 2, RpcMethodKind.Unary, "issue410-composite", null, null);
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseConcurrency(1024);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1_000_000_000;
            rate.TokensPerPeriod = 1_000_000_000;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });
        _controller = SharpLinkAdmissionController.Create(options, []);
    }

    [Benchmark]
    public void ConcurrencyAndRateImmediatePermit()
    {
        var decision = _controller.AcquireAsync(_context, 1, false, CancellationToken.None).Result;
        if (!decision.IsAcquired)
            throw new InvalidOperationException("composite benchmark unexpectedly rejected");
        decision.Lease!.Dispose();
    }

    [GlobalCleanup]
    public ValueTask Cleanup() => _controller.DisposeAsync();
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class Issue410UpdateControlBenchmarks
{
    private ISharpLinkServer _server = null!;
    private bool _fixed;

    [GlobalSetup]
    public void Setup()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        _server = builder.Build();
        _server.EnableAdmissionControl(options => ConfigureToken(options, 1_000_000_000));
    }

    [Benchmark]
    public void ReplaceRateAlgorithm()
    {
        _fixed = !_fixed;
        if (_fixed)
        {
            _server.UpdateAdmissionControl(options => options.Global.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 1_000_000_000;
                rate.Window = TimeSpan.FromHours(1);
            }));
        }
        else
        {
            _server.UpdateAdmissionControl(options => ConfigureToken(options, 1_000_000_000));
        }
    }

    [GlobalCleanup]
    public ValueTask Cleanup() => _server.DisposeAsync();

    private static void ConfigureToken(SharpLinkAdmissionControlOptions options, int limit)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = limit;
            rate.TokensPerPeriod = 10_000;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });
}
