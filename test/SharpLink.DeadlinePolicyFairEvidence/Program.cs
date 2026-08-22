using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class Program
{
    private const int Bps = 10_000;
    private static readonly ReadOnlySequence<byte> Payload = new(new byte[sizeof(int)]);

    private static readonly Scenario[] Scenarios =
    [
        new("A-dense-0-single", Priority.Primary, 1, 64, 65_536, Bps, 0, 30_000, 10, Pattern.Clustered, "client-default"),
        new("A-dense-0-concurrent", Priority.Primary, 4, 64, 65_536, Bps, 0, 30_000, 10, Pattern.Clustered, "client-default"),
        new("A-dense-0.01pct-expiry", Priority.Primary, 4, 128, 65_536, Bps, 1, 30_000, 10, Pattern.Clustered, "client-default"),
        new("A-dense-0.1pct-expiry", Priority.Primary, 4, 128, 65_536, Bps, 10, 30_000, 10, Pattern.Clustered, "client-default"),
        new("A-dense-1pct-expiry", Priority.Primary, 4, 128, 65_536, Bps, 100, 30_000, 10, Pattern.Clustered, "client-default"),
        new("A-dense-1024-inflight", Priority.Primary, 4, 256, 65_536, Bps, 10, 30_000, 10, Pattern.Clustered, "client-default"),
        new("B-zero-deadline-single", Priority.Primary, 1, 64, 65_536, 0, 0, 0, 0, Pattern.Clustered, "none"),
        new("B-zero-deadline-concurrent", Priority.Primary, 4, 256, 65_536, 0, 0, 0, 0, Pattern.Clustered, "none"),
        new("B-density-1pct", Priority.Primary, 4, 128, 65_536, 100, 100, 30_000, 10, Pattern.Clustered, "CallOptions.Deadline"),
        new("B-density-10pct", Priority.Primary, 4, 128, 65_536, 1_000, 100, 30_000, 10, Pattern.Clustered, "CallOptions.Timeout"),
        new("B-density-50pct", Priority.Primary, 4, 128, 65_536, 5_000, 100, 30_000, 10, Pattern.Clustered, "method-[Timeout]"),
        new("B-density-100pct", Priority.Primary, 4, 128, 65_536, Bps, 100, 30_000, 10, Pattern.Clustered, "CallOptions.Deadline"),
        new("P2-capacity-1k", Priority.Boundary, 4, 128, 1_024, 1_000, 100, 30_000, 10, Pattern.Clustered, "explicit"),
        new("P2-capacity-16k", Priority.Boundary, 4, 128, 16_384, 1_000, 100, 30_000, 10, Pattern.Clustered, "explicit"),
        new("P2-capacity-65k", Priority.Boundary, 4, 128, 65_536, 1_000, 100, 30_000, 10, Pattern.Clustered, "explicit"),
        new("P2-capacity-1m", Priority.Boundary, 4, 128, 1_048_576, 1_000, 100, 30_000, 10, Pattern.Clustered, "explicit"),
        new("P3-clustered-50pct", Priority.Stress, 4, 64, 65_536, Bps, 5_000, 30_000, 5, Pattern.Clustered, "explicit"),
        new("P3-clustered-100pct", Priority.Stress, 4, 64, 65_536, Bps, Bps, 30_000, 5, Pattern.Clustered, "explicit"),
        new("P3-staggered-100pct", Priority.Stress, 4, 64, 65_536, Bps, Bps, 30_000, 5, Pattern.Staggered, "explicit"),
        new("P3-high-concurrency", Priority.Stress, 4, 256, 65_536, Bps, Bps, 30_000, 5, Pattern.Clustered, "explicit")
    ];

    public static async Task Main(string[] args)
    {
        var primaryRounds = GetInt(args, "--primary-rounds", 3);
        var boundaryRounds = GetInt(args, "--boundary-rounds", 1);
        var warmupSeconds = GetInt(args, "--warmup-seconds", 1);
        var durationSeconds = GetInt(args, "--duration-seconds", 2);
        var jsonPath = GetString(args, "--json") ?? "artifacts/issue-280/fair-deadline-policy-evidence.json";
        if (primaryRounds <= 0 || boundaryRounds <= 0 || warmupSeconds <= 0 || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(args));

        WarmJit();
        var results = new List<RoundResult>();
        foreach (var scenario in Scenarios)
        {
            var rounds = scenario.Priority == Priority.Primary ? primaryRounds : boundaryRounds;
            for (var round = 1; round <= rounds; round++)
            {
                var order = round % 2 == 0
                    ? new[] { Scheduler.PerCallRuntimeTimer, Scheduler.SharedScan }
                    : new[] { Scheduler.SharedScan, Scheduler.PerCallRuntimeTimer };
                foreach (var scheduler in order)
                {
                    var result = RunRound(scenario, scheduler, round, warmupSeconds, durationSeconds);
                    results.Add(result);
                    Console.WriteLine(JsonSerializer.Serialize(new { kind = "round", result }));
                }
            }
        }

        var summaries = Summarize(results);
        foreach (var summary in summaries)
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "summary", summary }));
        var report = new Report(
            1,
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            primaryRounds,
            boundaryRounds,
            warmupSeconds,
            durationSeconds,
            "Per-call lane has no timer-counting wrapper allocation. Timer create/dispose counts are workload-derived; only real callbacks increment a counter. Normal deadline-bearing calls use 30s due times; selected expiries use short due times to bound CI wall-clock.",
            results.ToArray(),
            summaries);
        var directory = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static RoundResult RunRound(Scenario scenario, Scheduler scheduler, int round, int warmupSeconds, int durationSeconds)
    {
        var owner = new Owner(scenario.Capacity);
        CallbackCountingTimeProvider? sharedProvider = null;
        FairTimerFactory? perCallFactory = null;
        TimeProvider tableProvider;
        if (scheduler == Scheduler.SharedScan)
        {
            sharedProvider = new CallbackCountingTimeProvider();
            tableProvider = sharedProvider;
        }
        else
        {
            perCallFactory = new FairTimerFactory();
            tableProvider = NoDeadlineSchedulerTimeProvider.Instance;
        }

        using var table = new PendingRequestTable(scenario.Capacity, IntCodecProvider.Instance, owner, tableProvider);
        _ = RunPhase(table, owner, sharedProvider, perCallFactory, scenario, scheduler, TimeSpan.FromSeconds(warmupSeconds), false);
        EnsureDrained(table, owner, "warmup");
        owner.Reset();
        sharedProvider?.Reset();
        perCallFactory?.Reset();
        ForceGc();
        var phase = RunPhase(table, owner, sharedProvider, perCallFactory, scenario, scheduler, TimeSpan.FromSeconds(durationSeconds), true);
        EnsureDrained(table, owner, "measure");

        var callbacks = scheduler == Scheduler.SharedScan ? sharedProvider!.Callbacks : perCallFactory!.Callbacks;
        var timerCreates = scheduler == Scheduler.PerCallRuntimeTimer ? phase.DeadlineRegistrations : 0;
        var timerDisposes = scheduler == Scheduler.PerCallRuntimeTimer ? phase.DeadlineRegistrations : 0;
        return new RoundResult(
            scenario.Name,
            scenario.Priority.ToString(),
            scenario.Source,
            scheduler.ToString(),
            round,
            scenario.Workers,
            scenario.BatchSize,
            scenario.Capacity,
            scenario.DeadlineDensityBps / 100d,
            scenario.ExpiryBpsOfDeadline / 100d,
            scenario.Pattern.ToString(),
            phase.ElapsedSeconds,
            phase.Completed,
            phase.DeadlineRegistrations,
            phase.NormalCompletions,
            phase.DeadlineCompletions,
            phase.Completed / phase.ElapsedSeconds,
            phase.CpuSeconds * 1_000_000_000d / phase.Completed,
            phase.CpuSeconds / phase.ElapsedSeconds,
            phase.AllocatedBytes / (double)phase.Completed,
            phase.Gen0Collections,
            phase.Gen1Collections,
            phase.Gen2Collections,
            timerCreates,
            timerDisposes,
            callbacks,
            callbacks / phase.ElapsedSeconds,
            phase.P95LatenessMs,
            phase.P99LatenessMs,
            phase.MaxLatenessMs);
    }

    private static PhaseResult RunPhase(
        PendingRequestTable table,
        Owner owner,
        CallbackCountingTimeProvider? sharedProvider,
        FairTimerFactory? perCallFactory,
        Scenario scenario,
        Scheduler scheduler,
        TimeSpan duration,
        bool measure)
    {
        using var gate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(scenario.Workers);
        var states = new WorkerState[scenario.Workers];
        var tasks = new Task<WorkerResult>[scenario.Workers];
        for (var worker = 0; worker < scenario.Workers; worker++)
        {
            var state = new WorkerState(table, owner, perCallFactory, scenario, scheduler, gate, ready, worker);
            states[worker] = state;
            tasks[worker] = Task.Factory.StartNew(
                static boxed => RunWorkerAsync((WorkerState)boxed!),
                state,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
        ready.Wait();
        using var process = Process.GetCurrentProcess();
        var allocBefore = measure ? GC.GetTotalAllocatedBytes(true) : 0;
        var g0Before = measure ? GC.CollectionCount(0) : 0;
        var g1Before = measure ? GC.CollectionCount(1) : 0;
        var g2Before = measure ? GC.CollectionCount(2) : 0;
        var cpuBefore = measure ? process.TotalProcessorTime : TimeSpan.Zero;
        var start = TimeProvider.System.GetTimestamp();
        var stop = start + ToTicks(duration);
        foreach (var state in states)
            state.StopTimestamp = stop;
        gate.Set();
        Task.WaitAll(tasks);
        var end = TimeProvider.System.GetTimestamp();
        var cpuAfter = measure ? process.TotalProcessorTime : TimeSpan.Zero;
        var allocAfter = measure ? GC.GetTotalAllocatedBytes(true) : 0;
        var g0After = measure ? GC.CollectionCount(0) : 0;
        var g1After = measure ? GC.CollectionCount(1) : 0;
        var g2After = measure ? GC.CollectionCount(2) : 0;

        long completed = 0;
        long registrations = 0;
        long normal = 0;
        long deadline = 0;
        foreach (var task in tasks)
        {
            var row = task.GetAwaiter().GetResult();
            completed += row.Completed;
            registrations += row.DeadlineRegistrations;
            normal += row.NormalCompletions;
            deadline += row.DeadlineCompletions;
        }
        var snapshot = owner.Snapshot();
        if (snapshot.Registered != completed || snapshot.Completed != completed || snapshot.Normal != normal || snapshot.Deadline != deadline)
            throw new InvalidOperationException($"owner mismatch: workers={completed}/{normal}/{deadline}, owner={snapshot.Registered}/{snapshot.Completed}/{snapshot.Normal}/{snapshot.Deadline}");
        if (snapshot.MissingDeadlineTracking != 0)
            throw new InvalidOperationException($"missing deadline tracking: {snapshot.MissingDeadlineTracking}");
        _ = sharedProvider;
        return new PhaseResult(
            (end - start) / (double)TimeProvider.System.TimestampFrequency,
            completed,
            registrations,
            normal,
            deadline,
            measure ? (cpuAfter - cpuBefore).TotalSeconds : 0,
            measure ? allocAfter - allocBefore : 0,
            measure ? g0After - g0Before : 0,
            measure ? g1After - g1Before : 0,
            measure ? g2After - g2Before : 0,
            snapshot.P95,
            snapshot.P99,
            snapshot.Max);
    }

    private static async Task<WorkerResult> RunWorkerAsync(WorkerState state)
    {
        state.Ready.Signal();
        state.Gate.Wait();
        long completed = 0;
        long deadlineRegistrations = 0;
        long normal = 0;
        long expired = 0;
        while (TimeProvider.System.GetTimestamp() < state.StopTimestamp)
        {
            var deadlineCount = state.DeadlineDensity.Next(state.Scenario.BatchSize);
            var expireCount = state.ExpiryDensity.Next(deadlineCount);
            for (var index = 0; index < state.Scenario.BatchSize; index++)
            {
                var hasDeadline = index < deadlineCount;
                var willExpire = index < expireCount;
                var now = TimeProvider.System.GetTimestamp();
                var dueMs = hasDeadline
                    ? willExpire ? ExpiryMilliseconds(state.Scenario, index) : state.Scenario.NormalDeadlineMs
                    : 0;
                var dueTimestamp = hasDeadline ? now + ToTicks(TimeSpan.FromMilliseconds(dueMs)) : 0;
                RpcDeadline deadline = default;
                if (hasDeadline && state.Scheduler == Scheduler.SharedScan)
                {
                    var remaining = Remaining(dueTimestamp, TimeProvider.System.GetTimestamp());
                    deadline = RpcDeadline.Create(TimeProvider.System.GetUtcNow().Add(remaining), TimeProvider.System);
                }

                state.Operations[index] = state.Table.Rent(
                    IntCodec.Instance,
                    PendingCallKind.Unary,
                    deadline,
                    CancellationToken.None,
                    out state.RequestIds[index]);
                if (hasDeadline)
                {
                    deadlineRegistrations++;
                    if (willExpire)
                        state.Owner.TrackDeadline(state.RequestIds[index], dueTimestamp);
                    if (state.Scheduler == Scheduler.PerCallRuntimeTimer)
                    {
                        state.Registrations[index] = new PerCallRegistration(
                            state.Table,
                            state.RequestIds[index],
                            dueTimestamp,
                            state.TimerFactory!);
                    }
                }
            }

            for (var index = expireCount; index < state.Scenario.BatchSize; index++)
            {
                var payload = Payload;
                var dispatched = state.Table.Dispatch(state.RequestIds[index], ref payload);
                state.Registrations[index]?.Dispose();
                state.Registrations[index] = null;
                if (!dispatched)
                    throw new InvalidOperationException("normal response lost pending call");
                _ = state.Operations[index].AsValueTask().GetAwaiter().GetResult();
                normal++;
            }
            for (var index = 0; index < expireCount; index++)
            {
                try
                {
                    _ = await state.Operations[index].AsValueTask().ConfigureAwait(false);
                    throw new InvalidOperationException("expected deadline");
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DeadlineExceeded)
                {
                    expired++;
                }
                finally
                {
                    state.Registrations[index]?.Dispose();
                    state.Registrations[index] = null;
                }
            }
            completed += state.Scenario.BatchSize;
        }
        return new WorkerResult(completed, deadlineRegistrations, normal, expired);
    }

    private static int ExpiryMilliseconds(Scenario scenario, int index)
        => scenario.Pattern == Pattern.Clustered ? scenario.ExpiryDeadlineMs : scenario.ExpiryDeadlineMs + index % 16;

    private static Summary[] Summarize(List<RoundResult> results)
    {
        var output = new List<Summary>();
        foreach (var scenario in Scenarios)
        {
            var shared = results.Where(r => r.Scenario == scenario.Name && r.Scheduler == Scheduler.SharedScan.ToString()).ToArray();
            var perCall = results.Where(r => r.Scenario == scenario.Name && r.Scheduler == Scheduler.PerCallRuntimeTimer.ToString()).ToArray();
            if (shared.Length == 0 || perCall.Length == 0)
                continue;
            var sharedQps = Median(shared.Select(r => r.Qps));
            var perCallQps = Median(perCall.Select(r => r.Qps));
            var sharedCpu = Median(shared.Select(r => r.CpuNsPerOp));
            var perCallCpu = Median(perCall.Select(r => r.CpuNsPerOp));
            var sharedAlloc = Median(shared.Select(r => r.AllocatedBytesPerOp));
            var perCallAlloc = Median(perCall.Select(r => r.AllocatedBytesPerOp));
            output.Add(new Summary(
                scenario.Name,
                scenario.Priority.ToString(),
                scenario.Source,
                scenario.Capacity,
                scenario.DeadlineDensityBps / 100d,
                scenario.ExpiryBpsOfDeadline / 100d,
                sharedQps,
                perCallQps,
                Delta(perCallQps, sharedQps),
                sharedCpu,
                perCallCpu,
                Delta(perCallCpu, sharedCpu),
                sharedAlloc,
                perCallAlloc,
                perCallAlloc - sharedAlloc,
                Median(shared.Select(r => r.CallbacksPerSecond)),
                Median(perCall.Select(r => r.CallbacksPerSecond)),
                Median(shared.Select(r => r.P95LatenessMs)),
                Median(perCall.Select(r => r.P95LatenessMs)),
                Median(shared.Select(r => r.P99LatenessMs)),
                Median(perCall.Select(r => r.P99LatenessMs))));
        }
        return output.ToArray();
    }

    private static void WarmJit()
    {
        using var table = new PendingRequestTable(64, IntCodecProvider.Instance, NoopOwner.Instance, TimeProvider.System);
        var op = table.Rent(IntCodec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var payload = Payload;
        if (!table.Dispatch(id, ref payload))
            throw new InvalidOperationException("warmup failed");
        _ = op.AsValueTask().GetAwaiter().GetResult();
    }

    private static void EnsureDrained(PendingRequestTable table, Owner owner, string phase)
    {
        if (table.Count != 0 || table.ActiveCount != 0)
            throw new InvalidOperationException($"{phase} leaked pending calls: {table.Count}/{table.ActiveCount}");
        owner.EnsureNoTracking(phase);
    }

    private static long ToTicks(TimeSpan duration)
        => checked((long)Math.Ceiling(duration.TotalSeconds * TimeProvider.System.TimestampFrequency));

    private static TimeSpan Remaining(long dueTimestamp, long now)
        => dueTimestamp <= now ? TimeSpan.Zero : TimeSpan.FromSeconds((dueTimestamp - now) / (double)TimeProvider.System.TimestampFrequency);

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string? GetString(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        return null;
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        var value = GetString(args, name);
        return value is null ? fallback : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0)
            return 0;
        var mid = a.Length / 2;
        return a.Length % 2 == 0 ? (a[mid - 1] + a[mid]) / 2 : a[mid];
    }

    private static double Delta(double candidate, double baseline)
        => baseline == 0 ? 0 : (candidate - baseline) * 100d / baseline;

    private sealed class WorkerState(
        PendingRequestTable table,
        Owner owner,
        FairTimerFactory? timerFactory,
        Scenario scenario,
        Scheduler scheduler,
        ManualResetEventSlim gate,
        CountdownEvent ready,
        int seed)
    {
        internal PendingRequestTable Table { get; } = table;
        internal Owner Owner { get; } = owner;
        internal FairTimerFactory? TimerFactory { get; } = timerFactory;
        internal Scenario Scenario { get; } = scenario;
        internal Scheduler Scheduler { get; } = scheduler;
        internal ManualResetEventSlim Gate { get; } = gate;
        internal CountdownEvent Ready { get; } = ready;
        internal RpcRequestOperation<int>[] Operations { get; } = new RpcRequestOperation<int>[scenario.BatchSize];
        internal long[] RequestIds { get; } = new long[scenario.BatchSize];
        internal PerCallRegistration?[] Registrations { get; } = new PerCallRegistration?[scenario.BatchSize];
        internal Density DeadlineDensity { get; } = new(scenario.DeadlineDensityBps, seed * 997);
        internal Density ExpiryDensity { get; } = new(scenario.ExpiryBpsOfDeadline, seed * 313);
        internal long StopTimestamp { get; set; }
    }

    private sealed class Density(int basisPoints, int seed)
    {
        private long _remainder = seed % Bps;
        internal int Next(int items)
        {
            if (items == 0 || basisPoints == 0)
                return 0;
            if (basisPoints == Bps)
                return items;
            var scaled = checked((long)items * basisPoints + _remainder);
            var result = (int)(scaled / Bps);
            _remainder = scaled % Bps;
            return result;
        }
    }

    private sealed class PerCallRegistration : IDisposable
    {
        private readonly PendingRequestTable _table;
        private readonly long _requestId;
        private readonly long _dueTimestamp;
        private readonly FairTimerFactory _factory;
        private ITimer? _timer;
        private int _disposed;

        internal PerCallRegistration(PendingRequestTable table, long requestId, long dueTimestamp, FairTimerFactory factory)
        {
            _table = table;
            _requestId = requestId;
            _dueTimestamp = dueTimestamp;
            _factory = factory;
            _timer = factory.Create(static state => ((PerCallRegistration)state!).OnTimer(), this, Remaining(dueTimestamp, factory.GetTimestamp()));
        }

        private void OnTimer()
        {
            _factory.RecordCallback();
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var now = _factory.GetTimestamp();
            if (now < _dueTimestamp)
            {
                _factory.RecordRearm();
                try
                {
                    Volatile.Read(ref _timer)?.Change(Remaining(_dueTimestamp, now), Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                }
                return;
            }
            _table.TryComplete(_requestId, PendingCallCompletionReason.DeadlineExceeded);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }
    }

    private sealed class FairTimerFactory
    {
        private long _callbacks;
        private long _rearms;
        internal long Callbacks => Volatile.Read(ref _callbacks);
        internal long Rearms => Volatile.Read(ref _rearms);
        internal long GetTimestamp() => TimeProvider.System.GetTimestamp();
        internal ITimer Create(TimerCallback callback, object state, TimeSpan dueTime)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, Timeout.InfiniteTimeSpan);
        internal void RecordCallback() => Interlocked.Increment(ref _callbacks);
        internal void RecordRearm() => Interlocked.Increment(ref _rearms);
        internal void Reset()
        {
            Interlocked.Exchange(ref _callbacks, 0);
            Interlocked.Exchange(ref _rearms, 0);
        }
    }

    private sealed class CallbackCountingTimeProvider : TimeProvider
    {
        private long _callbacks;
        internal long Callbacks => Volatile.Read(ref _callbacks);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var invocation = new SharedTimerState(this, callback, state);
            return TimeProvider.System.CreateTimer(
                static boxed =>
                {
                    var current = (SharedTimerState)boxed!;
                    Interlocked.Increment(ref current.Provider._callbacks);
                    current.Callback(current.State);
                },
                invocation,
                dueTime,
                period);
        }
        internal void Reset() => Interlocked.Exchange(ref _callbacks, 0);
        private sealed record SharedTimerState(CallbackCountingTimeProvider Provider, TimerCallback Callback, object? State);
    }

    private sealed class NoDeadlineSchedulerTimeProvider : TimeProvider
    {
        internal static NoDeadlineSchedulerTimeProvider Instance { get; } = new();
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => NoopTimer.Instance;
    }

    private sealed class NoopTimer : ITimer
    {
        internal static NoopTimer Instance { get; } = new();
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Owner : IPendingCallOwner
    {
        private readonly int _mask;
        private readonly long[] _ids;
        private readonly long[] _due;
        private readonly Histogram _histogram = new();
        private long _registered;
        private long _completed;
        private long _normal;
        private long _deadline;
        private long _missing;

        internal Owner(int capacity)
        {
            _mask = capacity - 1;
            _ids = new long[capacity];
            _due = new long[capacity];
        }
        internal void TrackDeadline(long id, long due)
        {
            var index = (int)(id & _mask);
            Volatile.Write(ref _due[index], due);
            Volatile.Write(ref _ids[index], id);
        }
        public void OnPendingCallRegistered() => Interlocked.Increment(ref _registered);
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            if (completion.Reason == PendingCallCompletionReason.DeadlineExceeded)
            {
                var index = (int)(completion.RequestId & _mask);
                var tracked = Interlocked.CompareExchange(ref _ids[index], 0, completion.RequestId) == completion.RequestId;
                var due = tracked ? Volatile.Read(ref _due[index]) : 0;
                if (tracked)
                    Volatile.Write(ref _due[index], 0);
                if (!tracked || due == 0)
                    Interlocked.Increment(ref _missing);
                else
                    _histogram.Record(TimeProvider.System.GetTimestamp() - due);
                Interlocked.Increment(ref _deadline);
            }
            else
            {
                Interlocked.Increment(ref _normal);
            }
            Interlocked.Increment(ref _completed);
        }
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw new InvalidOperationException("producer cancellation callback failed", exception);
        internal OwnerSnapshot Snapshot() => new(
            Volatile.Read(ref _registered),
            Volatile.Read(ref _completed),
            Volatile.Read(ref _normal),
            Volatile.Read(ref _deadline),
            Volatile.Read(ref _missing),
            _histogram.Percentile(0.95),
            _histogram.Percentile(0.99),
            _histogram.Max);
        internal void Reset()
        {
            EnsureNoTracking("reset");
            Interlocked.Exchange(ref _registered, 0);
            Interlocked.Exchange(ref _completed, 0);
            Interlocked.Exchange(ref _normal, 0);
            Interlocked.Exchange(ref _deadline, 0);
            Interlocked.Exchange(ref _missing, 0);
            _histogram.Clear();
        }
        internal void EnsureNoTracking(string phase)
        {
            for (var i = 0; i < _ids.Length; i++)
                if (Volatile.Read(ref _ids[i]) != 0)
                    throw new InvalidOperationException($"{phase} retained deadline tracking at {i}");
        }
    }

    private sealed class Histogram
    {
        private const int BucketUs = 10;
        private const int MaxMs = 1000;
        private const int BucketCount = MaxMs * 1000 / BucketUs + 1;
        private readonly long[] _counts = new long[BucketCount + 1];
        private long _total;
        private int _maxBucket;
        internal double Max => Math.Min(Volatile.Read(ref _maxBucket), BucketCount) * BucketUs / 1000d;
        internal void Record(long timestampDelta)
        {
            var us = Math.Max(0, timestampDelta) * 1_000_000d / TimeProvider.System.TimestampFrequency;
            var bucket = Math.Min(BucketCount, (int)Math.Ceiling(us / BucketUs));
            Interlocked.Increment(ref _counts[bucket]);
            Interlocked.Increment(ref _total);
            while (true)
            {
                var current = Volatile.Read(ref _maxBucket);
                if (current >= bucket || Interlocked.CompareExchange(ref _maxBucket, bucket, current) == current)
                    break;
            }
        }
        internal double Percentile(double p)
        {
            var total = Volatile.Read(ref _total);
            if (total == 0)
                return 0;
            var target = (long)Math.Ceiling(total * p);
            long seen = 0;
            for (var i = 0; i < _counts.Length; i++)
            {
                seen += Volatile.Read(ref _counts[i]);
                if (seen >= target)
                    return Math.Min(i, BucketCount) * BucketUs / 1000d;
            }
            return MaxMs;
        }
        internal void Clear()
        {
            Array.Clear(_counts);
            Volatile.Write(ref _total, 0);
            Volatile.Write(ref _maxBucket, 0);
        }
    }

    private sealed class IntCodecProvider : IRpcCodecProvider
    {
        internal static IntCodecProvider Instance { get; } = new();
        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)IntCodec.Instance;
            throw new NotSupportedException(typeof(T).FullName);
        }
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        internal static IntCodec Instance { get; } = new();
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            buffer.CopyTo(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
    }

    private sealed class NoopOwner : IPendingCallOwner
    {
        internal static NoopOwner Instance { get; } = new();
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { _ = completion; }
        public void OnProducerCancellationCallbackFailed(Exception exception) { _ = exception; }
    }

    private enum Scheduler { SharedScan, PerCallRuntimeTimer }
    private enum Priority { Primary, Boundary, Stress }
    private enum Pattern { Clustered, Staggered }
    private sealed record Scenario(string Name, Priority Priority, int Workers, int BatchSize, int Capacity, int DeadlineDensityBps, int ExpiryBpsOfDeadline, int NormalDeadlineMs, int ExpiryDeadlineMs, Pattern Pattern, string Source);
    private sealed record WorkerResult(long Completed, long DeadlineRegistrations, long NormalCompletions, long DeadlineCompletions);
    private sealed record OwnerSnapshot(long Registered, long Completed, long Normal, long Deadline, long MissingDeadlineTracking, double P95, double P99, double Max);
    private sealed record PhaseResult(double ElapsedSeconds, long Completed, long DeadlineRegistrations, long NormalCompletions, long DeadlineCompletions, double CpuSeconds, long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, double P95LatenessMs, double P99LatenessMs, double MaxLatenessMs);
    private sealed record RoundResult(string Scenario, string Priority, string Source, string Scheduler, int Round, int Workers, int BatchSize, int Capacity, double DeadlineDensityPercent, double ExpiryPercentOfDeadlineCalls, string Pattern, double ElapsedSeconds, long Completed, long DeadlineRegistrations, long NormalCompletions, long DeadlineCompletions, double Qps, double CpuNsPerOp, double EffectiveCpuCores, double AllocatedBytesPerOp, int Gen0Collections, int Gen1Collections, int Gen2Collections, long TimerCreates, long TimerDisposes, long TimerCallbacks, double CallbacksPerSecond, double P95LatenessMs, double P99LatenessMs, double MaxLatenessMs);
    private sealed record Summary(string Scenario, string Priority, string Source, int Capacity, double DeadlineDensityPercent, double ExpiryPercentOfDeadlineCalls, double SharedMedianQps, double PerCallMedianQps, double PerCallQpsDeltaPercent, double SharedMedianCpuNsPerOp, double PerCallMedianCpuNsPerOp, double PerCallCpuDeltaPercent, double SharedMedianAllocatedBytesPerOp, double PerCallMedianAllocatedBytesPerOp, double PerCallAllocationDeltaBytesPerOp, double SharedMedianCallbacksPerSecond, double PerCallMedianCallbacksPerSecond, double SharedMedianP95LatenessMs, double PerCallMedianP95LatenessMs, double SharedMedianP99LatenessMs, double PerCallMedianP99LatenessMs);
    private sealed record Report(int SchemaVersion, string RuntimeVersion, int ProcessorCount, int PrimaryRounds, int BoundaryRounds, int WarmupSeconds, int DurationSeconds, string MethodologyNote, RoundResult[] Results, Summary[] Summaries);
}
