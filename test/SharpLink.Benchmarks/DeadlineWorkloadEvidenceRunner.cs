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

/// <summary>
/// End-to-end deadline workload evidence for issue #252. The workload intentionally charges deadline
/// registration, normal completion, real timer-driven scans, deadline completion, CPU, allocation, and
/// observed deadline lateness together. It is intended to be run unchanged on dev and candidate builds.
/// </summary>
internal static class DeadlineWorkloadEvidenceRunner
{
    private const int Capacity = 65_536;
    private static readonly ReadOnlySequence<byte> CompletionPayload = new(new byte[sizeof(int)]);

    private static readonly Scenario[] Scenarios =
    [
        new("single-fast", 1, 64, 5, 10),
        new("concurrent-fast", 4, 64, 5, 10),
        new("concurrent-normal", 4, 128, 20, 10),
        new("deadline-heavy", 4, 64, 5, 50)
    ];

    public static async Task RunAsync(string[] args)
    {
        var rounds = GetInt32(args, "--rounds", 3);
        var warmupSeconds = GetInt32(args, "--warmup-seconds", 2);
        var durationSeconds = GetInt32(args, "--duration-seconds", 8);
        var jsonPath = GetString(args, "--json");
        var productionBaselineSha = GetString(args, "--production-baseline-sha") ?? "unknown";

        if (rounds <= 0 || warmupSeconds <= 0 || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "Rounds, warmup, and duration must be positive.");

        WarmUpJit();
        var results = new List<RoundResult>(Scenarios.Length * rounds);
        foreach (var scenario in Scenarios)
        {
            for (var round = 1; round <= rounds; round++)
            {
                var result = RunScenarioRound(scenario, round, warmupSeconds, durationSeconds);
                results.Add(result);
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "round", result }));
            }
        }

        var summaries = Scenarios.Select(scenario => Summarize(scenario, results)).ToArray();
        foreach (var summary in summaries)
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "summary", summary }));

        var report = new EvidenceReport(
            2,
            productionBaselineSha,
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            rounds,
            warmupSeconds,
            durationSeconds,
            results.ToArray(),
            summaries);

        if (jsonPath is not null)
        {
            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);
        }
    }

    private static RoundResult RunScenarioRound(
        Scenario scenario,
        int round,
        int warmupSeconds,
        int durationSeconds)
    {
        var timeProvider = new CountingTimeProvider();
        var owner = new EvidenceOwner(Capacity, timeProvider);
        using var table = new PendingRequestTable(
            Capacity,
            Int32CodecProvider.Instance,
            owner,
            timeProvider);

        _ = RunPhase(table, owner, timeProvider, scenario, TimeSpan.FromSeconds(warmupSeconds), captureMetrics: false);
        EnsureTableDrained(table, owner, "warmup");
        owner.ResetMeasurements();
        timeProvider.ResetTimerCallbackCount();
        ForceFullGc();

        var measured = RunPhase(
            table,
            owner,
            timeProvider,
            scenario,
            TimeSpan.FromSeconds(durationSeconds),
            captureMetrics: true);
        EnsureTableDrained(table, owner, "measurement");

        return new RoundResult(
            scenario.Name,
            round,
            scenario.Workers,
            scenario.BatchSize,
            scenario.DeadlineMilliseconds,
            scenario.ExpirePercent,
            measured.ElapsedSeconds,
            measured.Completed,
            measured.NormalCompletions,
            measured.DeadlineCompletions,
            measured.Completed / measured.ElapsedSeconds,
            measured.CpuSeconds,
            measured.CpuSeconds * 1_000_000_000d / measured.Completed,
            measured.CpuSeconds / measured.ElapsedSeconds,
            measured.AllocatedBytes,
            measured.AllocatedBytes / (double)measured.Completed,
            measured.TimerCallbacks,
            measured.TimerCallbacks / measured.ElapsedSeconds,
            measured.P95LatenessMilliseconds,
            measured.P99LatenessMilliseconds,
            measured.MaxLatenessMilliseconds,
            measured.LatenessOverflowCount);
    }

    private static PhaseResult RunPhase(
        PendingRequestTable table,
        EvidenceOwner owner,
        CountingTimeProvider timeProvider,
        Scenario scenario,
        TimeSpan duration,
        bool captureMetrics)
    {
        using var gate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(scenario.Workers);
        var workers = new WorkerState[scenario.Workers];
        var tasks = new Task<WorkerResult>[scenario.Workers];
        for (var index = 0; index < workers.Length; index++)
        {
            var state = new WorkerState(table, owner, timeProvider, scenario, gate, ready);
            workers[index] = state;
            tasks[index] = Task.Factory.StartNew(
                static boxed => RunWorkerAsync((WorkerState)boxed!),
                state,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        ready.Wait();
        using var process = Process.GetCurrentProcess();
        var timerCallbacksBefore = timeProvider.TimerCallbackCount;
        var allocationsBefore = captureMetrics ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var cpuBefore = captureMetrics ? process.TotalProcessorTime : TimeSpan.Zero;
        var started = timeProvider.GetTimestamp();
        var stopTimestamp = started + ToTimestampTicks(duration, timeProvider.TimestampFrequency);
        foreach (var worker in workers)
            worker.StopTimestamp = stopTimestamp;
        gate.Set();

        Task.WaitAll(tasks);
        var stopped = timeProvider.GetTimestamp();
        var cpuAfter = captureMetrics ? process.TotalProcessorTime : TimeSpan.Zero;
        var allocationsAfter = captureMetrics ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var timerCallbacksAfter = timeProvider.TimerCallbackCount;

        long completed = 0;
        long normalCompletions = 0;
        long deadlineCompletions = 0;
        foreach (var task in tasks)
        {
            var worker = task.GetAwaiter().GetResult();
            completed += worker.Completed;
            normalCompletions += worker.NormalCompletions;
            deadlineCompletions += worker.DeadlineCompletions;
        }

        var ownerSnapshot = owner.Snapshot();
        if (completed == 0 || deadlineCompletions == 0)
            throw new InvalidOperationException("Deadline workload produced no measurable completions.");
        if (ownerSnapshot.Registered != completed || ownerSnapshot.Completed != completed)
        {
            throw new InvalidOperationException(
                $"Owner accounting mismatch: workers={completed}, registered={ownerSnapshot.Registered}, completed={ownerSnapshot.Completed}.");
        }
        if (ownerSnapshot.DeadlineCompletions != deadlineCompletions ||
            ownerSnapshot.NormalCompletions != normalCompletions)
        {
            throw new InvalidOperationException(
                $"Completion accounting mismatch: worker normal/deadline={normalCompletions}/{deadlineCompletions}, " +
                $"owner={ownerSnapshot.NormalCompletions}/{ownerSnapshot.DeadlineCompletions}.");
        }
        if (ownerSnapshot.MissingDeadlineTracking != 0)
        {
            throw new InvalidOperationException(
                $"Deadline completion raced ahead of evidence tracking {ownerSnapshot.MissingDeadlineTracking} time(s).");
        }
        if (ownerSnapshot.ProducerCancellationFailures != 0)
        {
            throw new InvalidOperationException(
                $"Unexpected producer-cancellation callback failures: {ownerSnapshot.ProducerCancellationFailures}.");
        }

        var elapsedSeconds = (stopped - started) / (double)timeProvider.TimestampFrequency;
        return new PhaseResult(
            elapsedSeconds,
            completed,
            normalCompletions,
            deadlineCompletions,
            captureMetrics ? (cpuAfter - cpuBefore).TotalSeconds : 0,
            captureMetrics ? allocationsAfter - allocationsBefore : 0,
            timerCallbacksAfter - timerCallbacksBefore,
            ownerSnapshot.P95LatenessMilliseconds,
            ownerSnapshot.P99LatenessMilliseconds,
            ownerSnapshot.MaxLatenessMilliseconds,
            ownerSnapshot.LatenessOverflowCount);
    }

    private static async Task<WorkerResult> RunWorkerAsync(WorkerState state)
    {
        state.Ready.Signal();
        state.Gate.Wait();

        long completed = 0;
        long normalCompletions = 0;
        long deadlineCompletions = 0;
        var expireCount = Math.Max(
            1,
            (state.Scenario.BatchSize * state.Scenario.ExpirePercent + 99) / 100);

        while (state.TimeProvider.GetTimestamp() < state.StopTimestamp)
        {
            var started = state.TimeProvider.GetTimestamp();
            var dueTimestamp = started + ToTimestampTicks(
                TimeSpan.FromMilliseconds(state.Scenario.DeadlineMilliseconds),
                state.TimeProvider.TimestampFrequency);
            var deadline = RpcDeadline.Create(
                state.TimeProvider.GetUtcNow().AddMilliseconds(state.Scenario.DeadlineMilliseconds),
                state.TimeProvider);

            for (var index = 0; index < state.Scenario.BatchSize; index++)
            {
                state.Operations[index] = state.Table.Rent(
                    Int32Codec.Instance,
                    PendingCallKind.Unary,
                    deadline,
                    CancellationToken.None,
                    out state.RequestIds[index]);
                state.Owner.TrackDeadline(state.RequestIds[index], dueTimestamp);
            }

            for (var index = expireCount; index < state.Scenario.BatchSize; index++)
            {
                var payload = CompletionPayload;
                if (!state.Table.Dispatch(state.RequestIds[index], ref payload))
                    throw new InvalidOperationException("A normal deadline workload completion lost its pending request.");
                _ = state.Operations[index].AsValueTask().GetAwaiter().GetResult();
                normalCompletions++;
            }

            for (var index = 0; index < expireCount; index++)
            {
                try
                {
                    _ = await state.Operations[index].AsValueTask().ConfigureAwait(false);
                    throw new InvalidOperationException("A deadline workload timeout completed successfully.");
                }
                catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
                {
                    deadlineCompletions++;
                }
            }

            completed += state.Scenario.BatchSize;
        }

        return new WorkerResult(completed, normalCompletions, deadlineCompletions);
    }

    private static ScenarioSummary Summarize(Scenario scenario, List<RoundResult> allResults)
    {
        var results = allResults.Where(result => result.Scenario == scenario.Name).ToArray();
        return new ScenarioSummary(
            scenario.Name,
            Median(results.Select(static result => result.Qps)),
            Median(results.Select(static result => result.CpuNanosecondsPerOperation)),
            Median(results.Select(static result => result.EffectiveCpuCores)),
            Median(results.Select(static result => result.AllocatedBytesPerOperation)),
            Median(results.Select(static result => result.TimerCallbacksPerSecond)),
            Median(results.Select(static result => result.P95LatenessMilliseconds)),
            Median(results.Select(static result => result.P99LatenessMilliseconds)),
            Median(results.Select(static result => result.MaxLatenessMilliseconds)));
    }

    private static void WarmUpJit()
    {
        var timeProvider = new CountingTimeProvider();
        using var table = new PendingRequestTable(
            64,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            timeProvider);
        var deadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddMilliseconds(2), timeProvider);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            deadline,
            CancellationToken.None,
            out var requestId);
        var payload = CompletionPayload;
        if (!table.Dispatch(requestId, ref payload))
            throw new InvalidOperationException("Deadline workload JIT warm-up failed.");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void EnsureTableDrained(PendingRequestTable table, EvidenceOwner owner, string phase)
    {
        if (table.Count != 0 || table.ActiveCount != 0)
        {
            throw new InvalidOperationException(
                $"Deadline workload {phase} leaked pending calls: Count={table.Count}, ActiveCount={table.ActiveCount}.");
        }
        owner.EnsureNoOutstandingTracking(phase);
    }

    private static long ToTimestampTicks(TimeSpan duration, long frequency)
        => checked((long)Math.Ceiling(duration.TotalSeconds * frequency));

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int GetInt32(string[] args, string name, int defaultValue)
    {
        var value = GetString(args, name);
        return value is null
            ? defaultValue
            : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string? GetString(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private sealed class WorkerState(
        PendingRequestTable table,
        EvidenceOwner owner,
        CountingTimeProvider timeProvider,
        Scenario scenario,
        ManualResetEventSlim gate,
        CountdownEvent ready)
    {
        internal PendingRequestTable Table { get; } = table;
        internal EvidenceOwner Owner { get; } = owner;
        internal CountingTimeProvider TimeProvider { get; } = timeProvider;
        internal Scenario Scenario { get; } = scenario;
        internal ManualResetEventSlim Gate { get; } = gate;
        internal CountdownEvent Ready { get; } = ready;
        internal RpcRequestOperation<int>[] Operations { get; } = new RpcRequestOperation<int>[scenario.BatchSize];
        internal long[] RequestIds { get; } = new long[scenario.BatchSize];
        internal long StopTimestamp { get; set; }
    }

    private sealed class EvidenceOwner : IPendingCallOwner
    {
        private readonly int _indexMask;
        private readonly CountingTimeProvider _timeProvider;
        private readonly long[] _trackedRequestIds;
        private readonly long[] _dueTimestamps;
        private readonly LatenessHistogram _histogram = new();
        private long _registered;
        private long _completed;
        private long _deadlineCompletions;
        private long _normalCompletions;
        private long _missingDeadlineTracking;
        private long _producerCancellationFailures;

        internal EvidenceOwner(int capacity, CountingTimeProvider timeProvider)
        {
            _indexMask = capacity - 1;
            _timeProvider = timeProvider;
            _trackedRequestIds = new long[capacity];
            _dueTimestamps = new long[capacity];
        }

        internal void TrackDeadline(long requestId, long dueTimestamp)
        {
            var index = (int)(requestId & _indexMask);
            Volatile.Write(ref _dueTimestamps[index], dueTimestamp);
            Volatile.Write(ref _trackedRequestIds[index], requestId);
        }

        public void OnPendingCallRegistered() => Interlocked.Increment(ref _registered);

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            var index = (int)(completion.RequestId & _indexMask);
            var tracked = Interlocked.CompareExchange(
                ref _trackedRequestIds[index],
                0,
                completion.RequestId) == completion.RequestId;
            var dueTimestamp = tracked ? Volatile.Read(ref _dueTimestamps[index]) : 0;
            if (tracked)
                Volatile.Write(ref _dueTimestamps[index], 0);

            if (completion.Reason == PendingCallCompletionReason.DeadlineExceeded)
            {
                if (!tracked || dueTimestamp == 0)
                {
                    Interlocked.Increment(ref _missingDeadlineTracking);
                }
                else
                {
                    _histogram.RecordTimestampDelta(
                        _timeProvider.GetTimestamp() - dueTimestamp,
                        _timeProvider.TimestampFrequency);
                }
                Interlocked.Increment(ref _deadlineCompletions);
            }
            else
            {
                Interlocked.Increment(ref _normalCompletions);
            }

            Interlocked.Increment(ref _completed);
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
            => Interlocked.Increment(ref _producerCancellationFailures);

        internal OwnerSnapshot Snapshot()
            => new(
                Volatile.Read(ref _registered),
                Volatile.Read(ref _completed),
                Volatile.Read(ref _deadlineCompletions),
                Volatile.Read(ref _normalCompletions),
                Volatile.Read(ref _missingDeadlineTracking),
                Volatile.Read(ref _producerCancellationFailures),
                _histogram.PercentileMilliseconds(0.95),
                _histogram.PercentileMilliseconds(0.99),
                _histogram.MaxObservedMilliseconds,
                _histogram.OverflowCount);

        internal void ResetMeasurements()
        {
            EnsureNoOutstandingTracking("reset");
            Interlocked.Exchange(ref _registered, 0);
            Interlocked.Exchange(ref _completed, 0);
            Interlocked.Exchange(ref _deadlineCompletions, 0);
            Interlocked.Exchange(ref _normalCompletions, 0);
            Interlocked.Exchange(ref _missingDeadlineTracking, 0);
            Interlocked.Exchange(ref _producerCancellationFailures, 0);
            _histogram.Clear();
        }

        internal void EnsureNoOutstandingTracking(string phase)
        {
            for (var index = 0; index < _trackedRequestIds.Length; index++)
            {
                if (Volatile.Read(ref _trackedRequestIds[index]) != 0)
                    throw new InvalidOperationException($"Deadline workload {phase} retained request tracking at slot {index}.");
            }
        }
    }

    private sealed class LatenessHistogram
    {
        private const int BucketMicroseconds = 10;
        private const int MaxTrackedMilliseconds = 1000;
        private const int BucketCount = MaxTrackedMilliseconds * 1000 / BucketMicroseconds + 1;
        private readonly long[] _counts = new long[BucketCount + 1];
        private long _total;
        private int _maxBucket;

        internal long OverflowCount => Volatile.Read(ref _counts[^1]);

        internal double MaxObservedMilliseconds
        {
            get
            {
                var maxBucket = Volatile.Read(ref _maxBucket);
                return maxBucket >= BucketCount
                    ? MaxTrackedMilliseconds
                    : maxBucket * BucketMicroseconds / 1000d;
            }
        }

        internal void RecordTimestampDelta(long timestampDelta, long frequency)
        {
            var latenessMicroseconds = Math.Max(0, timestampDelta) * 1_000_000d / frequency;
            var bucket = (int)Math.Ceiling(latenessMicroseconds / BucketMicroseconds);
            if (bucket >= BucketCount)
                bucket = BucketCount;
            Interlocked.Increment(ref _counts[bucket]);
            Interlocked.Increment(ref _total);
            UpdateMaxBucket(bucket);
        }

        internal double PercentileMilliseconds(double percentile)
        {
            var total = Volatile.Read(ref _total);
            if (total == 0)
                return 0;
            var target = (long)Math.Ceiling(total * percentile);
            long seen = 0;
            for (var index = 0; index < _counts.Length; index++)
            {
                seen += Volatile.Read(ref _counts[index]);
                if (seen >= target)
                {
                    return index >= BucketCount
                        ? MaxTrackedMilliseconds
                        : index * BucketMicroseconds / 1000d;
                }
            }
            return MaxTrackedMilliseconds;
        }

        internal void Clear()
        {
            Array.Clear(_counts);
            Volatile.Write(ref _total, 0);
            Volatile.Write(ref _maxBucket, 0);
        }

        private void UpdateMaxBucket(int bucket)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxBucket);
                if (current >= bucket)
                    return;
                if (Interlocked.CompareExchange(ref _maxBucket, bucket, current) == current)
                    return;
            }
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private long _timerCallbackCount;

        internal long TimerCallbackCount => Volatile.Read(ref _timerCallbackCount);

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var invocation = new TimerInvocation(this, callback, state);
            return TimeProvider.System.CreateTimer(
                static boxed =>
                {
                    var current = (TimerInvocation)boxed!;
                    Interlocked.Increment(ref current.Provider._timerCallbackCount);
                    current.Callback(current.State);
                },
                invocation,
                dueTime,
                period);
        }

        internal void ResetTimerCallbackCount() => Interlocked.Exchange(ref _timerCallbackCount, 0);

        private sealed record TimerInvocation(
            CountingTimeProvider Provider,
            TimerCallback Callback,
            object? State);
    }

    private sealed class Int32CodecProvider : IRpcCodecProvider
    {
        internal static Int32CodecProvider Instance { get; } = new();

        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)Int32Codec.Instance;
            throw new NotSupportedException(typeof(T).FullName);
        }
    }

    private sealed class Int32Codec : IRpcCodec<int>
    {
        internal static Int32Codec Instance { get; } = new();

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

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }

    private sealed record Scenario(
        string Name,
        int Workers,
        int BatchSize,
        int DeadlineMilliseconds,
        int ExpirePercent);

    private sealed record WorkerResult(
        long Completed,
        long NormalCompletions,
        long DeadlineCompletions);

    private sealed record OwnerSnapshot(
        long Registered,
        long Completed,
        long DeadlineCompletions,
        long NormalCompletions,
        long MissingDeadlineTracking,
        long ProducerCancellationFailures,
        double P95LatenessMilliseconds,
        double P99LatenessMilliseconds,
        double MaxLatenessMilliseconds,
        long LatenessOverflowCount);

    private sealed record PhaseResult(
        double ElapsedSeconds,
        long Completed,
        long NormalCompletions,
        long DeadlineCompletions,
        double CpuSeconds,
        long AllocatedBytes,
        long TimerCallbacks,
        double P95LatenessMilliseconds,
        double P99LatenessMilliseconds,
        double MaxLatenessMilliseconds,
        long LatenessOverflowCount);

    private sealed record RoundResult(
        string Scenario,
        int Round,
        int Workers,
        int BatchSize,
        int DeadlineMilliseconds,
        int ExpirePercent,
        double ElapsedSeconds,
        long Completed,
        long NormalCompletions,
        long DeadlineCompletions,
        double Qps,
        double CpuSeconds,
        double CpuNanosecondsPerOperation,
        double EffectiveCpuCores,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        long TimerCallbacks,
        double TimerCallbacksPerSecond,
        double P95LatenessMilliseconds,
        double P99LatenessMilliseconds,
        double MaxLatenessMilliseconds,
        long LatenessOverflowCount);

    private sealed record ScenarioSummary(
        string Scenario,
        double MedianQps,
        double MedianCpuNanosecondsPerOperation,
        double MedianEffectiveCpuCores,
        double MedianAllocatedBytesPerOperation,
        double MedianTimerCallbacksPerSecond,
        double MedianP95LatenessMilliseconds,
        double MedianP99LatenessMilliseconds,
        double MedianMaxLatenessMilliseconds);

    private sealed record EvidenceReport(
        int SchemaVersion,
        string ProductionBaselineSha,
        string RuntimeVersion,
        int ProcessorCount,
        int Rounds,
        int WarmupSeconds,
        int DurationSeconds,
        RoundResult[] Results,
        ScenarioSummary[] Summaries);
}
