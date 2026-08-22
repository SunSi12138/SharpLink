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
    private const int BasisPoints = 10_000;
    private static readonly ReadOnlySequence<byte> CompletionPayload = new(new byte[sizeof(int)]);

    private static readonly Scenario[] Scenarios =
    [
        // P0-A: default/equivalent timeout enabled. Normal calls carry a 30s safety-fuse deadline;
        // only the selected expiry fraction uses a short deadline so the evidence run is bounded.
        new("A-dense-0-single", EvidencePriority.Primary, 1, 64, 65_536, BasisPoints, 0, 30_000, 10, ExpiryPattern.Clustered, "client-default"),
        new("A-dense-0-concurrent", EvidencePriority.Primary, 4, 64, 65_536, BasisPoints, 0, 30_000, 10, ExpiryPattern.Clustered, "client-default"),
        new("A-dense-0.01pct-expiry", EvidencePriority.Primary, 4, 128, 65_536, BasisPoints, 1, 30_000, 10, ExpiryPattern.Clustered, "client-default"),
        new("A-dense-0.1pct-expiry", EvidencePriority.Primary, 4, 128, 65_536, BasisPoints, 10, 30_000, 10, ExpiryPattern.Clustered, "client-default"),
        new("A-dense-1pct-expiry", EvidencePriority.Primary, 4, 128, 65_536, BasisPoints, 100, 30_000, 10, ExpiryPattern.Clustered, "client-default"),
        new("A-dense-1024-inflight", EvidencePriority.Primary, 4, 256, 65_536, BasisPoints, 10, 30_000, 10, ExpiryPattern.Clustered, "client-default"),

        // P0-B: DisableRequestTimeout() + no explicit deadline.
        new("B-zero-deadline-single", EvidencePriority.Primary, 1, 64, 65_536, 0, 0, 0, 0, ExpiryPattern.Clustered, "none"),
        new("B-zero-deadline-concurrent", EvidencePriority.Primary, 4, 256, 65_536, 0, 0, 0, 0, ExpiryPattern.Clustered, "none"),

        // P1: explicit deadline density. The scheduler receives the same effective RpcDeadline
        // regardless of source; source labels make that normalization explicit in the report.
        new("B-density-1pct", EvidencePriority.Primary, 4, 128, 65_536, 100, 100, 30_000, 10, ExpiryPattern.Clustered, "CallOptions.Deadline"),
        new("B-density-10pct", EvidencePriority.Primary, 4, 128, 65_536, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "CallOptions.Timeout"),
        new("B-density-50pct", EvidencePriority.Primary, 4, 128, 65_536, 5_000, 100, 30_000, 10, ExpiryPattern.Clustered, "method-[Timeout]"),
        new("B-density-100pct", EvidencePriority.Primary, 4, 128, 65_536, BasisPoints, 100, 30_000, 10, ExpiryPattern.Clustered, "CallOptions.Deadline"),
        new("B-source-method-timeout", EvidencePriority.Boundary, 4, 128, 65_536, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "method-[Timeout]"),
        new("B-source-call-timeout", EvidencePriority.Boundary, 4, 128, 65_536, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "CallOptions.Timeout"),
        new("B-source-call-deadline", EvidencePriority.Boundary, 4, 128, 65_536, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "CallOptions.Deadline"),

        // P2: same active/deadline-bearing call shape, capacity only changes scan width.
        new("P2-capacity-1k", EvidencePriority.Boundary, 4, 128, 1_024, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "explicit"),
        new("P2-capacity-16k", EvidencePriority.Boundary, 4, 128, 16_384, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "explicit"),
        new("P2-capacity-65k", EvidencePriority.Boundary, 4, 128, 65_536, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "explicit"),
        new("P2-capacity-1m", EvidencePriority.Boundary, 4, 128, 1_048_576, 1_000, 100, 30_000, 10, ExpiryPattern.Clustered, "explicit"),

        // P3: degradation boundaries; these do not carry the same decision weight as P0/P1.
        new("P3-clustered-50pct-expiry", EvidencePriority.Stress, 4, 64, 65_536, BasisPoints, 5_000, 30_000, 5, ExpiryPattern.Clustered, "explicit"),
        new("P3-clustered-100pct-expiry", EvidencePriority.Stress, 4, 64, 65_536, BasisPoints, BasisPoints, 30_000, 5, ExpiryPattern.Clustered, "explicit"),
        new("P3-staggered-100pct-expiry", EvidencePriority.Stress, 4, 64, 65_536, BasisPoints, BasisPoints, 30_000, 5, ExpiryPattern.Staggered, "explicit"),
        new("P3-high-concurrency", EvidencePriority.Stress, 4, 256, 65_536, BasisPoints, BasisPoints, 30_000, 5, ExpiryPattern.Clustered, "explicit"),
        new("P3-long-lived", EvidencePriority.Stress, 4, 64, 65_536, BasisPoints, 100, 30_000, 100, ExpiryPattern.Staggered, "explicit")
    ];

    public static async Task<int> Main(string[] args)
    {
        var runCorrectness = HasFlag(args, "--correctness") || !HasFlag(args, "--benchmark");
        var runBenchmark = HasFlag(args, "--benchmark") || !HasFlag(args, "--correctness");
        var jsonPath = GetString(args, "--json") ?? "artifacts/issue-280/deadline-policy-evidence.json";
        var roundsPrimary = GetInt32(args, "--primary-rounds", 3);
        var roundsBoundary = GetInt32(args, "--boundary-rounds", 1);
        var warmupSeconds = GetInt32(args, "--warmup-seconds", 1);
        var durationSeconds = GetInt32(args, "--duration-seconds", 2);

        if (roundsPrimary <= 0 || roundsBoundary <= 0 || warmupSeconds <= 0 || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "Rounds, warmup, and duration must be positive.");

        CorrectnessReport? correctness = null;
        if (runCorrectness)
        {
            correctness = RunCorrectnessSuite();
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "correctness", correctness }));
        }

        EvidenceReport? evidence = null;
        if (runBenchmark)
        {
            WarmUpJit();
            var results = new List<RoundResult>();
            foreach (var scenario in Scenarios)
            {
                var rounds = scenario.Priority == EvidencePriority.Primary ? roundsPrimary : roundsBoundary;
                for (var round = 1; round <= rounds; round++)
                {
                    // Alternate order to reduce systematic first-run / thermal bias while keeping each pair on one runner.
                    var first = round % 2 == 0 ? SchedulerKind.PerCallRuntimeTimer : SchedulerKind.SharedScan;
                    var second = first == SchedulerKind.SharedScan ? SchedulerKind.PerCallRuntimeTimer : SchedulerKind.SharedScan;
                    foreach (var scheduler in new[] { first, second })
                    {
                        var result = RunScenarioRound(
                            scenario,
                            scheduler,
                            round,
                            warmupSeconds,
                            durationSeconds);
                        results.Add(result);
                        Console.WriteLine(JsonSerializer.Serialize(new { kind = "round", result }));
                    }
                }
            }

            var summaries = Summarize(results);
            var allocationGuardrail = RunZeroDeadlineAllocationGuardrail();
            var executionContextProbe = ProbeExecutionContextCapture();
            evidence = new EvidenceReport(
                1,
                Environment.Version.ToString(),
                Environment.ProcessorCount,
                roundsPrimary,
                roundsBoundary,
                warmupSeconds,
                durationSeconds,
                "Normal deadline-bearing completions use a 30s due time. Selected expiries use short due times to bound CI wall-clock while preserving timer create/cancel and real expiry callback costs.",
                allocationGuardrail,
                executionContextProbe,
                results.ToArray(),
                summaries);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "allocation-guardrail", allocationGuardrail }));
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "execution-context", executionContextProbe }));
            foreach (var summary in summaries)
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "summary", summary }));
        }

        var report = new CombinedReport(correctness, evidence);
        var directory = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        return 0;
    }

    private static RoundResult RunScenarioRound(
        Scenario scenario,
        SchedulerKind scheduler,
        int round,
        int warmupSeconds,
        int durationSeconds)
    {
        var owner = new EvidenceOwner(scenario.Capacity, TimeProvider.System);
        CountingTimeProvider? sharedTimeProvider = null;
        TimeProvider tableTimeProvider;
        SystemDeadlineTimerFactory? runtimeTimerFactory = null;

        if (scheduler == SchedulerKind.SharedScan)
        {
            sharedTimeProvider = new CountingTimeProvider();
            tableTimeProvider = sharedTimeProvider;
        }
        else
        {
            tableTimeProvider = NoDeadlineSchedulerTimeProvider.Instance;
            runtimeTimerFactory = new SystemDeadlineTimerFactory();
        }

        using var table = new PendingRequestTable(
            scenario.Capacity,
            Int32CodecProvider.Instance,
            owner,
            tableTimeProvider);

        _ = RunPhase(
            table,
            owner,
            sharedTimeProvider,
            runtimeTimerFactory,
            scenario,
            scheduler,
            TimeSpan.FromSeconds(warmupSeconds),
            captureMetrics: false);
        EnsureTableDrained(table, owner, "warmup");
        owner.ResetMeasurements();
        sharedTimeProvider?.ResetCounters();
        runtimeTimerFactory?.ResetCounters();
        ForceFullGc();

        var measured = RunPhase(
            table,
            owner,
            sharedTimeProvider,
            runtimeTimerFactory,
            scenario,
            scheduler,
            TimeSpan.FromSeconds(durationSeconds),
            captureMetrics: true);
        EnsureTableDrained(table, owner, "measurement");

        var timer = scheduler == SchedulerKind.SharedScan
            ? sharedTimeProvider!.Snapshot()
            : runtimeTimerFactory!.Snapshot();

        return new RoundResult(
            scenario.Name,
            scenario.Priority.ToString(),
            scenario.DeadlineSource,
            scheduler.ToString(),
            round,
            scenario.Workers,
            scenario.BatchSize,
            scenario.Capacity,
            scenario.DeadlineDensityBasisPoints / 100d,
            scenario.ExpiryBasisPointsOfDeadlineCalls / 100d,
            scenario.ExpiryPattern.ToString(),
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
            measured.Gen0Collections,
            measured.Gen1Collections,
            measured.Gen2Collections,
            timer.Creates,
            timer.Changes,
            timer.Disposes,
            timer.Callbacks,
            timer.Callbacks / measured.ElapsedSeconds,
            measured.P95LatenessMilliseconds,
            measured.P99LatenessMilliseconds,
            measured.MaxLatenessMilliseconds,
            measured.LatenessOverflowCount);
    }

    private static PhaseResult RunPhase(
        PendingRequestTable table,
        EvidenceOwner owner,
        CountingTimeProvider? sharedTimeProvider,
        SystemDeadlineTimerFactory? runtimeTimerFactory,
        Scenario scenario,
        SchedulerKind scheduler,
        TimeSpan duration,
        bool captureMetrics)
    {
        using var gate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(scenario.Workers);
        var states = new WorkerState[scenario.Workers];
        var tasks = new Task<WorkerResult>[scenario.Workers];
        for (var index = 0; index < scenario.Workers; index++)
        {
            var state = new WorkerState(
                table,
                owner,
                runtimeTimerFactory,
                scenario,
                scheduler,
                gate,
                ready,
                index);
            states[index] = state;
            tasks[index] = Task.Factory.StartNew(
                static boxed => RunWorkerAsync((WorkerState)boxed!),
                state,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        ready.Wait();
        using var process = Process.GetCurrentProcess();
        var allocationsBefore = captureMetrics ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var gen0Before = captureMetrics ? GC.CollectionCount(0) : 0;
        var gen1Before = captureMetrics ? GC.CollectionCount(1) : 0;
        var gen2Before = captureMetrics ? GC.CollectionCount(2) : 0;
        var cpuBefore = captureMetrics ? process.TotalProcessorTime : TimeSpan.Zero;
        var started = TimeProvider.System.GetTimestamp();
        var stopTimestamp = started + ToTimestampTicks(duration, TimeProvider.System.TimestampFrequency);
        foreach (var state in states)
            state.StopTimestamp = stopTimestamp;
        gate.Set();

        Task.WaitAll(tasks);
        var stopped = TimeProvider.System.GetTimestamp();
        var cpuAfter = captureMetrics ? process.TotalProcessorTime : TimeSpan.Zero;
        var allocationsAfter = captureMetrics ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        var gen0After = captureMetrics ? GC.CollectionCount(0) : 0;
        var gen1After = captureMetrics ? GC.CollectionCount(1) : 0;
        var gen2After = captureMetrics ? GC.CollectionCount(2) : 0;

        long completed = 0;
        long normal = 0;
        long deadline = 0;
        foreach (var task in tasks)
        {
            var result = task.GetAwaiter().GetResult();
            completed += result.Completed;
            normal += result.NormalCompletions;
            deadline += result.DeadlineCompletions;
        }

        var ownerSnapshot = owner.Snapshot();
        if (completed == 0)
            throw new InvalidOperationException("Evidence workload produced no completions.");
        if (ownerSnapshot.Registered != completed || ownerSnapshot.Completed != completed)
        {
            throw new InvalidOperationException(
                $"Owner accounting mismatch: workers={completed}, registered={ownerSnapshot.Registered}, completed={ownerSnapshot.Completed}.");
        }
        if (ownerSnapshot.DeadlineCompletions != deadline || ownerSnapshot.NormalCompletions != normal)
        {
            throw new InvalidOperationException(
                $"Completion accounting mismatch: worker normal/deadline={normal}/{deadline}, owner={ownerSnapshot.NormalCompletions}/{ownerSnapshot.DeadlineCompletions}.");
        }
        if (ownerSnapshot.MissingDeadlineTracking != 0)
            throw new InvalidOperationException($"Missing deadline tracking {ownerSnapshot.MissingDeadlineTracking} time(s).");
        if (ownerSnapshot.ProducerCancellationFailures != 0)
            throw new InvalidOperationException($"Producer cancellation callback failed {ownerSnapshot.ProducerCancellationFailures} time(s).");

        var elapsedSeconds = (stopped - started) / (double)TimeProvider.System.TimestampFrequency;
        _ = sharedTimeProvider;
        return new PhaseResult(
            elapsedSeconds,
            completed,
            normal,
            deadline,
            captureMetrics ? (cpuAfter - cpuBefore).TotalSeconds : 0,
            captureMetrics ? allocationsAfter - allocationsBefore : 0,
            captureMetrics ? gen0After - gen0Before : 0,
            captureMetrics ? gen1After - gen1Before : 0,
            captureMetrics ? gen2After - gen2Before : 0,
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
        long normal = 0;
        long deadline = 0;
        while (TimeProvider.System.GetTimestamp() < state.StopTimestamp)
        {
            var deadlineCount = state.DeadlineDensity.NextCount(state.Scenario.BatchSize);
            var expireCount = state.ExpiryDensity.NextCount(deadlineCount);
            var batchStarted = TimeProvider.System.GetTimestamp();

            for (var index = 0; index < state.Scenario.BatchSize; index++)
            {
                var deadlineBearing = index < deadlineCount;
                var shouldExpire = index < expireCount;
                var dueTimestamp = 0L;
                RpcDeadline rpcDeadline = default;
                if (deadlineBearing)
                {
                    var dueMilliseconds = shouldExpire
                        ? GetExpiryMilliseconds(state.Scenario, index)
                        : state.Scenario.NormalDeadlineMilliseconds;
                    dueTimestamp = batchStarted + ToTimestampTicks(
                        TimeSpan.FromMilliseconds(dueMilliseconds),
                        TimeProvider.System.TimestampFrequency);
                    if (state.Scheduler == SchedulerKind.SharedScan)
                    {
                        rpcDeadline = RpcDeadline.Create(
                            TimeProvider.System.GetUtcNow().AddMilliseconds(dueMilliseconds),
                            TimeProvider.System);
                    }
                }

                state.Operations[index] = state.Table.Rent(
                    Int32Codec.Instance,
                    PendingCallKind.Unary,
                    rpcDeadline,
                    CancellationToken.None,
                    out state.RequestIds[index]);

                if (deadlineBearing)
                {
                    state.Owner.TrackDeadline(state.RequestIds[index], dueTimestamp);
                    if (state.Scheduler == SchedulerKind.PerCallRuntimeTimer)
                    {
                        state.Registrations[index] = PerCallDeadlineRegistration.Create(
                            state.Table,
                            state.RequestIds[index],
                            dueTimestamp,
                            state.RuntimeTimerFactory!);
                    }
                }
            }

            for (var index = expireCount; index < state.Scenario.BatchSize; index++)
            {
                var payload = CompletionPayload;
                var dispatched = state.Table.Dispatch(state.RequestIds[index], ref payload);
                state.Registrations[index]?.Dispose();
                state.Registrations[index] = null;
                if (!dispatched)
                    throw new InvalidOperationException("A normal completion lost its pending request.");
                _ = state.Operations[index].AsValueTask().GetAwaiter().GetResult();
                normal++;
            }

            for (var index = 0; index < expireCount; index++)
            {
                try
                {
                    _ = await state.Operations[index].AsValueTask().ConfigureAwait(false);
                    throw new InvalidOperationException("An expiring request completed successfully.");
                }
                catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
                {
                    deadline++;
                }
                finally
                {
                    state.Registrations[index]?.Dispose();
                    state.Registrations[index] = null;
                }
            }

            completed += state.Scenario.BatchSize;
        }

        return new WorkerResult(completed, normal, deadline);
    }

    private static int GetExpiryMilliseconds(Scenario scenario, int index)
        => scenario.ExpiryPattern == ExpiryPattern.Clustered
            ? scenario.ExpiryDeadlineMilliseconds
            : scenario.ExpiryDeadlineMilliseconds + index % 16;

    private static ScenarioSummary[] Summarize(List<RoundResult> results)
    {
        var summaries = new List<ScenarioSummary>();
        foreach (var scenario in Scenarios)
        {
            var shared = results
                .Where(result => result.Scenario == scenario.Name && result.Scheduler == SchedulerKind.SharedScan.ToString())
                .ToArray();
            var perCall = results
                .Where(result => result.Scenario == scenario.Name && result.Scheduler == SchedulerKind.PerCallRuntimeTimer.ToString())
                .ToArray();
            if (shared.Length == 0 || perCall.Length == 0)
                continue;

            var sharedQps = Median(shared.Select(static result => result.Qps));
            var perCallQps = Median(perCall.Select(static result => result.Qps));
            var sharedCpu = Median(shared.Select(static result => result.CpuNanosecondsPerOperation));
            var perCallCpu = Median(perCall.Select(static result => result.CpuNanosecondsPerOperation));
            var sharedAlloc = Median(shared.Select(static result => result.AllocatedBytesPerOperation));
            var perCallAlloc = Median(perCall.Select(static result => result.AllocatedBytesPerOperation));
            summaries.Add(new ScenarioSummary(
                scenario.Name,
                scenario.Priority.ToString(),
                scenario.DeadlineSource,
                scenario.Capacity,
                scenario.DeadlineDensityBasisPoints / 100d,
                scenario.ExpiryBasisPointsOfDeadlineCalls / 100d,
                sharedQps,
                perCallQps,
                PercentDelta(perCallQps, sharedQps),
                sharedCpu,
                perCallCpu,
                PercentDelta(perCallCpu, sharedCpu),
                sharedAlloc,
                perCallAlloc,
                perCallAlloc - sharedAlloc,
                Median(shared.Select(static result => result.TimerCallbacksPerSecond)),
                Median(perCall.Select(static result => result.TimerCallbacksPerSecond)),
                Median(shared.Select(static result => result.P95LatenessMilliseconds)),
                Median(perCall.Select(static result => result.P95LatenessMilliseconds)),
                Median(shared.Select(static result => result.P99LatenessMilliseconds)),
                Median(perCall.Select(static result => result.P99LatenessMilliseconds)),
                Median(perCall.Select(static result => (double)result.TimerCreates)),
                Median(perCall.Select(static result => (double)result.TimerDisposes))));
        }
        return summaries.ToArray();
    }

    private static CorrectnessReport RunCorrectnessSuite()
    {
        var tests = new List<CorrectnessCase>();
        RunCase(tests, "no-deadline creates no per-call timer", TestNoDeadlineCreatesNoTimer);
        RunCase(tests, "cancellation-only remains cancellation", TestCancellationOnly);
        RunCase(tests, "intentionally unbounded call stays pending", TestIntentionallyUnbounded);
        RunCase(tests, "deadline before one tick does not fire", TestBeforeDeadlineDoesNotFire);
        RunCase(tests, "exact deadline fires", TestExactDeadlineFires);
        RunCase(tests, "early callback rearms", TestEarlyCallbackRearms);
        RunCase(tests, "response wins timer callback no-op", TestResponseWins);
        RunCase(tests, "cancel wins timer callback no-op", TestCancelWins);
        RunCase(tests, "deadline wins later response no-op", TestDeadlineWins);
        RunCase(tests, "disconnect wins timer callback no-op", TestDisconnectWins);
        RunCase(tests, "normal and deadline race exactly once", TestTerminalRace);
        RunCase(tests, "dispose and callback race safe", TestDisposeCallbackRace);
        RunCase(tests, "stale timer cannot hit reused slot", TestStaleTimerAfterReuse);
        RunCase(tests, "table dispose isolates live timer", TestTableDisposeWithLiveTimer);
        RunCase(tests, "large fake-time jump expires batch", TestLargeFakeTimeJump);
        RunCase(tests, "100k timer churn retains no active timers", TestTimerChurn);
        var failed = tests.Count(static test => !test.Passed);
        if (failed != 0)
            throw new InvalidOperationException($"Issue #280 correctness suite failed {failed} case(s).\n" + string.Join("\n", tests.Where(static test => !test.Passed).Select(static test => test.Name + ": " + test.Error)));
        return new CorrectnessReport(tests.Count, failed, tests.ToArray());
    }

    private static void RunCase(List<CorrectnessCase> tests, string name, Action test)
    {
        try
        {
            test();
            tests.Add(new CorrectnessCase(name, true, null));
        }
        catch (Exception exception)
        {
            tests.Add(new CorrectnessCase(name, false, exception.ToString()));
        }
    }

    private static void TestNoDeadlineCreatesNoTimer()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var payload = CompletionPayload;
        Assert(table.Dispatch(id, ref payload), "normal dispatch failed");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
        Assert(factory.CreateCount == 0, "no-deadline path created a per-call timer");
    }

    private static void TestCancellationOnly()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        using var cancellation = new CancellationTokenSource();
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, cancellation.Token, out _);
        cancellation.Cancel();
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("cancellation-only operation completed successfully");
        }
        catch (OperationCanceledException)
        {
        }
        Assert(factory.CreateCount == 0, "cancellation-only path created a deadline timer");
    }

    private static void TestIntentionallyUnbounded()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        factory.Advance(TimeSpan.FromDays(365));
        Assert(table.Contains(id), "unbounded operation was completed by framework time");
        var payload = CompletionPayload;
        Assert(table.Dispatch(id, ref payload), "unbounded normal dispatch failed");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void TestBeforeDeadlineDoesNotFire()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var due = factory.GetTimestamp() + 10;
        using var registration = PerCallDeadlineRegistration.Create(table, id, due, factory);
        factory.AdvanceTicks(9);
        factory.FireAll();
        Assert(table.Contains(id), "deadline fired one tick early");
        var payload = CompletionPayload;
        Assert(table.Dispatch(id, ref payload), "normal dispatch failed");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void TestExactDeadlineFires()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var due = factory.GetTimestamp() + 10;
        using var registration = PerCallDeadlineRegistration.Create(table, id, due, factory);
        factory.AdvanceTicks(10);
        factory.FireAll();
        AssertDeadline(operation);
        Assert(!table.Contains(id), "exact deadline left call pending");
    }

    private static void TestEarlyCallbackRearms()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var due = factory.GetTimestamp() + 100;
        using var registration = PerCallDeadlineRegistration.Create(table, id, due, factory);
        factory.AdvanceTicks(10);
        factory.FireAll();
        Assert(table.Contains(id), "early callback completed call");
        Assert(factory.ChangeCount > 0, "early callback did not rearm timer");
        factory.AdvanceTicks(90);
        factory.FireAll();
        AssertDeadline(operation);
    }

    private static void TestResponseWins()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 10, factory);
        var payload = CompletionPayload;
        Assert(table.Dispatch(id, ref payload), "response did not win");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
        factory.AdvanceTicks(10);
        factory.FireAll(includeDisposed: true);
        Assert(table.Count == 0, "stale callback changed completed response");
    }

    private static void TestCancelWins()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        using var cancellation = new CancellationTokenSource();
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, cancellation.Token, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 10, factory);
        cancellation.Cancel();
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("cancel did not win");
        }
        catch (OperationCanceledException)
        {
        }
        factory.AdvanceTicks(10);
        factory.FireAll(includeDisposed: true);
        Assert(table.Count == 0, "timer resurrected cancelled call");
    }

    private static void TestDeadlineWins()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 10, factory);
        factory.AdvanceTicks(10);
        factory.FireAll();
        AssertDeadline(operation);
        var payload = CompletionPayload;
        Assert(!table.Dispatch(id, ref payload), "late response completed deadline winner");
    }

    private static void TestDisconnectWins()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 10, factory);
        Assert(table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed), "disconnect did not complete call");
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("disconnect completed successfully");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
        {
        }
        factory.AdvanceTicks(10);
        factory.FireAll(includeDisposed: true);
        Assert(table.Count == 0, "timer affected disconnected lifecycle");
    }

    private static void TestTerminalRace()
    {
        var factory = new ManualDeadlineTimerFactory();
        var owner = new CountingOwner();
        using var table = new PendingRequestTable(64, Int32CodecProvider.Instance, owner, factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp(), factory);
        using var barrier = new Barrier(3);
        var responseWon = false;
        var responseTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            var payload = CompletionPayload;
            responseWon = table.Dispatch(id, ref payload);
        });
        var deadlineTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            factory.FireAll();
        });
        barrier.SignalAndWait();
        Task.WaitAll(responseTask, deadlineTask);
        Assert(owner.Completed == 1, $"terminal race completed {owner.Completed} times");
        if (responseWon)
            _ = operation.AsValueTask().GetAwaiter().GetResult();
        else
            AssertDeadline(operation);
    }

    private static void TestDisposeCallbackRace()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp(), factory);
        using var barrier = new Barrier(3);
        var disposeTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            registration.Dispose();
        });
        var callbackTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            factory.FireAll(includeDisposed: true);
        });
        barrier.SignalAndWait();
        Task.WaitAll(disposeTask, callbackTask);
        if (table.Contains(id))
        {
            var payload = CompletionPayload;
            Assert(table.Dispatch(id, ref payload), "post-race normal dispatch failed");
            _ = operation.AsValueTask().GetAwaiter().GetResult();
        }
        else
        {
            AssertDeadline(operation);
        }
        Assert(table.Count == 0, "dispose/callback race leaked call");
    }

    private static void TestStaleTimerAfterReuse()
    {
        var factory = new ManualDeadlineTimerFactory();
        using var table = new PendingRequestTable(1, Int32CodecProvider.Instance, NoopOwner.Instance, factory.Provider);
        var first = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var firstId);
        var oldRegistration = PerCallDeadlineRegistration.Create(table, firstId, factory.GetTimestamp() + 10, factory);
        var payload = CompletionPayload;
        Assert(table.Dispatch(firstId, ref payload), "first response failed");
        _ = first.AsValueTask().GetAwaiter().GetResult();

        var second = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var secondId);
        Assert(firstId != secondId, "request id did not advance");
        factory.AdvanceTicks(10);
        factory.FireAll(includeDisposed: true);
        Assert(table.Contains(secondId), "stale timer hit reused slot");
        oldRegistration.Dispose();
        payload = CompletionPayload;
        Assert(table.Dispatch(secondId, ref payload), "second response failed");
        _ = second.AsValueTask().GetAwaiter().GetResult();
    }

    private static void TestTableDisposeWithLiveTimer()
    {
        var factory = new ManualDeadlineTimerFactory();
        var table = CreateManualTable(factory.Provider);
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 10, factory);
        table.Dispose();
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("table dispose completed successfully");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
        {
        }
        factory.AdvanceTicks(10);
        factory.FireAll(includeDisposed: true);
    }

    private static void TestLargeFakeTimeJump()
    {
        const int count = 512;
        var factory = new ManualDeadlineTimerFactory();
        using var table = new PendingRequestTable(1_024, Int32CodecProvider.Instance, NoopOwner.Instance, factory.Provider);
        var operations = new RpcRequestOperation<int>[count];
        var registrations = new PerCallDeadlineRegistration[count];
        var due = factory.GetTimestamp() + 100;
        for (var index = 0; index < count; index++)
        {
            operations[index] = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
            registrations[index] = PerCallDeadlineRegistration.Create(table, id, due, factory);
        }
        factory.AdvanceTicks(10_000);
        factory.FireAll();
        for (var index = 0; index < count; index++)
        {
            AssertDeadline(operations[index]);
            registrations[index].Dispose();
        }
        Assert(table.Count == 0, "large time jump left pending calls");
    }

    private static void TestTimerChurn()
    {
        const int iterations = 100_000;
        var factory = new ManualDeadlineTimerFactory();
        using var table = new PendingRequestTable(64, Int32CodecProvider.Instance, NoopOwner.Instance, factory.Provider);
        for (var index = 0; index < iterations; index++)
        {
            var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
            using var registration = PerCallDeadlineRegistration.Create(table, id, factory.GetTimestamp() + 1_000, factory);
            var payload = CompletionPayload;
            Assert(table.Dispatch(id, ref payload), "churn dispatch failed");
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            if ((index & 0x3ff) == 0)
                factory.CompactDisposedTimers();
        }
        factory.CompactDisposedTimers();
        Assert(factory.ActiveTimerCount == 0, $"timer churn retained {factory.ActiveTimerCount} active timer(s)");
        Assert(table.Count == 0, "timer churn leaked pending calls");
    }

    private static ZeroDeadlineAllocationGuardrail RunZeroDeadlineAllocationGuardrail()
    {
        const int warmup = 10_000;
        const int iterations = 100_000;
        using var table = new PendingRequestTable(
            64,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            NoDeadlineSchedulerTimeProvider.Instance);
        for (var index = 0; index < warmup; index++)
            RunZeroDeadlineOperation(table);
        ForceFullGc();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
            RunZeroDeadlineOperation(table);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new ZeroDeadlineAllocationGuardrail(iterations, allocated, allocated / (double)iterations, 0, 0);
    }

    private static void RunZeroDeadlineOperation(PendingRequestTable table)
    {
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default, CancellationToken.None, out var id);
        var payload = CompletionPayload;
        if (!table.Dispatch(id, ref payload))
            throw new InvalidOperationException("zero-deadline guardrail dispatch failed");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static ExecutionContextProbe ProbeExecutionContextCapture()
    {
        var ambient = new AsyncLocal<string?>();
        string? observed = "not-run";
        using var fired = new ManualResetEventSlim(false);
        ambient.Value = "captured-value";
        using var timer = TimeProvider.System.CreateTimer(
            _ =>
            {
                observed = ambient.Value;
                fired.Set();
            },
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
        ambient.Value = null;
        if (!fired.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("ExecutionContext timer probe did not fire.");
        return new ExecutionContextProbe(observed is not null, observed);
    }

    private static PendingRequestTable CreateManualTable(TimeProvider provider)
        => new(64, Int32CodecProvider.Instance, NoopOwner.Instance, provider);

    private static void AssertDeadline(RpcRequestOperation<int> operation)
    {
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("expected DeadlineExceeded");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void WarmUpJit()
    {
        using var table = new PendingRequestTable(
            64,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            TimeProvider.System);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            default,
            CancellationToken.None,
            out var id);
        var payload = CompletionPayload;
        if (!table.Dispatch(id, ref payload))
            throw new InvalidOperationException("JIT warm-up failed.");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void EnsureTableDrained(PendingRequestTable table, EvidenceOwner owner, string phase)
    {
        if (table.Count != 0 || table.ActiveCount != 0)
            throw new InvalidOperationException($"{phase} leaked pending calls: Count={table.Count}, ActiveCount={table.ActiveCount}.");
        owner.EnsureNoOutstandingTracking(phase);
    }

    private static long ToTimestampTicks(TimeSpan duration, long frequency)
        => checked((long)Math.Ceiling(duration.TotalSeconds * frequency));

    private static TimeSpan Remaining(long deadlineTimestamp, long now, long frequency)
    {
        if (deadlineTimestamp <= now)
            return TimeSpan.Zero;
        var seconds = (deadlineTimestamp - now) / (double)frequency;
        return TimeSpan.FromSeconds(seconds);
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.Ordinal));

    private static int GetInt32(string[] args, string name, int defaultValue)
    {
        var value = GetString(args, name);
        return value is null ? defaultValue : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
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
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static double PercentDelta(double candidate, double baseline)
        => baseline == 0 ? 0 : (candidate - baseline) * 100d / baseline;

    private sealed class WorkerState(
        PendingRequestTable table,
        EvidenceOwner owner,
        SystemDeadlineTimerFactory? runtimeTimerFactory,
        Scenario scenario,
        SchedulerKind scheduler,
        ManualResetEventSlim gate,
        CountdownEvent ready,
        int workerIndex)
    {
        internal PendingRequestTable Table { get; } = table;
        internal EvidenceOwner Owner { get; } = owner;
        internal SystemDeadlineTimerFactory? RuntimeTimerFactory { get; } = runtimeTimerFactory;
        internal Scenario Scenario { get; } = scenario;
        internal SchedulerKind Scheduler { get; } = scheduler;
        internal ManualResetEventSlim Gate { get; } = gate;
        internal CountdownEvent Ready { get; } = ready;
        internal RpcRequestOperation<int>[] Operations { get; } = new RpcRequestOperation<int>[scenario.BatchSize];
        internal long[] RequestIds { get; } = new long[scenario.BatchSize];
        internal PerCallDeadlineRegistration?[] Registrations { get; } = new PerCallDeadlineRegistration?[scenario.BatchSize];
        internal DensityAccumulator DeadlineDensity { get; } = new(scenario.DeadlineDensityBasisPoints, workerIndex * 997);
        internal DensityAccumulator ExpiryDensity { get; } = new(scenario.ExpiryBasisPointsOfDeadlineCalls, workerIndex * 313);
        internal long StopTimestamp { get; set; }
    }

    private sealed class DensityAccumulator(int basisPoints, int seed)
    {
        private long _remainder = seed % BasisPoints;

        internal int NextCount(int itemCount)
        {
            if (itemCount == 0 || basisPoints == 0)
                return 0;
            if (basisPoints == BasisPoints)
                return itemCount;
            var scaled = checked((long)itemCount * basisPoints + _remainder);
            var count = (int)(scaled / BasisPoints);
            _remainder = scaled % BasisPoints;
            return count;
        }
    }

    private sealed class PerCallDeadlineRegistration : IDisposable
    {
        private readonly PendingRequestTable _table;
        private readonly long _requestId;
        private readonly long _deadlineTimestamp;
        private readonly IDeadlineTimerFactory _factory;
        private ITimer? _timer;
        private int _disposed;

        private PerCallDeadlineRegistration(
            PendingRequestTable table,
            long requestId,
            long deadlineTimestamp,
            IDeadlineTimerFactory factory)
        {
            _table = table;
            _requestId = requestId;
            _deadlineTimestamp = deadlineTimestamp;
            _factory = factory;
            _timer = factory.CreateTimer(
                static state => ((PerCallDeadlineRegistration)state!).OnTimer(),
                this,
                Remaining(deadlineTimestamp, factory.GetTimestamp(), factory.TimestampFrequency));
        }

        internal static PerCallDeadlineRegistration Create(
            PendingRequestTable table,
            long requestId,
            long deadlineTimestamp,
            IDeadlineTimerFactory factory)
            => new(table, requestId, deadlineTimestamp, factory);

        private void OnTimer()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var now = _factory.GetTimestamp();
            if (now < _deadlineTimestamp)
            {
                try
                {
                    Volatile.Read(ref _timer)?.Change(
                        Remaining(_deadlineTimestamp, now, _factory.TimestampFrequency),
                        Timeout.InfiniteTimeSpan);
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

    private interface IDeadlineTimerFactory
    {
        long TimestampFrequency { get; }
        long GetTimestamp();
        ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime);
    }

    private sealed class SystemDeadlineTimerFactory : IDeadlineTimerFactory
    {
        private long _creates;
        private long _changes;
        private long _disposes;
        private long _callbacks;

        public long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime)
        {
            Interlocked.Increment(ref _creates);
            var invocation = new TimerInvocation(this, callback, state);
            var inner = TimeProvider.System.CreateTimer(
                static boxed =>
                {
                    var current = (TimerInvocation)boxed!;
                    Interlocked.Increment(ref current.Factory._callbacks);
                    current.Callback(current.State);
                },
                invocation,
                dueTime,
                Timeout.InfiniteTimeSpan);
            return new CountingTimer(inner, this);
        }

        internal TimerCounters Snapshot()
            => new(
                Volatile.Read(ref _creates),
                Volatile.Read(ref _changes),
                Volatile.Read(ref _disposes),
                Volatile.Read(ref _callbacks));

        internal void ResetCounters()
        {
            Interlocked.Exchange(ref _creates, 0);
            Interlocked.Exchange(ref _changes, 0);
            Interlocked.Exchange(ref _disposes, 0);
            Interlocked.Exchange(ref _callbacks, 0);
        }

        private sealed record TimerInvocation(SystemDeadlineTimerFactory Factory, TimerCallback Callback, object State);

        private sealed class CountingTimer(ITimer inner, SystemDeadlineTimerFactory factory) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Interlocked.Increment(ref factory._changes);
                return inner.Change(dueTime, period);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref factory._disposes);
                inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref factory._disposes);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private long _creates;
        private long _changes;
        private long _disposes;
        private long _callbacks;

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _creates);
            var invocation = new ProviderTimerInvocation(this, callback, state);
            var inner = TimeProvider.System.CreateTimer(
                static boxed =>
                {
                    var current = (ProviderTimerInvocation)boxed!;
                    Interlocked.Increment(ref current.Provider._callbacks);
                    current.Callback(current.State);
                },
                invocation,
                dueTime,
                period);
            return new ProviderCountingTimer(inner, this);
        }

        internal TimerCounters Snapshot()
            => new(
                Volatile.Read(ref _creates),
                Volatile.Read(ref _changes),
                Volatile.Read(ref _disposes),
                Volatile.Read(ref _callbacks));

        internal void ResetCounters()
        {
            Interlocked.Exchange(ref _creates, 0);
            Interlocked.Exchange(ref _changes, 0);
            Interlocked.Exchange(ref _disposes, 0);
            Interlocked.Exchange(ref _callbacks, 0);
        }

        private sealed record ProviderTimerInvocation(CountingTimeProvider Provider, TimerCallback Callback, object? State);

        private sealed class ProviderCountingTimer(ITimer inner, CountingTimeProvider provider) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Interlocked.Increment(ref provider._changes);
                return inner.Change(dueTime, period);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref provider._disposes);
                inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref provider._disposes);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class NoDeadlineSchedulerTimeProvider : TimeProvider
    {
        internal static NoDeadlineSchedulerTimeProvider Instance { get; } = new();
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => NoopTimer.Instance;
    }

    private sealed class NoopTimer : ITimer
    {
        internal static NoopTimer Instance { get; } = new();
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualDeadlineTimerFactory : IDeadlineTimerFactory
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private long _creates;
        private long _changes;

        internal ManualDeadlineTimerFactory()
        {
            Provider = new ManualTableTimeProvider(this);
        }

        internal TimeProvider Provider { get; }
        public long TimestampFrequency => 1_000;
        public long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal long CreateCount => Volatile.Read(ref _creates);
        internal long ChangeCount => Volatile.Read(ref _changes);
        internal int ActiveTimerCount
        {
            get
            {
                lock (_gate)
                    return _timers.Count(static timer => !timer.IsDisposed);
            }
        }

        public ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime)
        {
            Interlocked.Increment(ref _creates);
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan duration)
            => AdvanceTicks(ToTimestampTicks(duration, TimestampFrequency));

        internal void AdvanceTicks(long ticks)
            => Interlocked.Add(ref _timestamp, ticks);

        internal void FireAll(bool includeDisposed = false)
        {
            ManualTimer[] snapshot;
            lock (_gate)
                snapshot = _timers.ToArray();
            foreach (var timer in snapshot)
                timer.Fire(includeDisposed);
        }

        internal void CompactDisposedTimers()
        {
            lock (_gate)
                _timers.RemoveAll(static timer => timer.IsDisposed);
        }

        private sealed class ManualTableTimeProvider(ManualDeadlineTimerFactory factory) : TimeProvider
        {
            public override long TimestampFrequency => factory.TimestampFrequency;
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(factory.GetTimestamp());
            public override long GetTimestamp() => factory.GetTimestamp();
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
                => NoopTimer.Instance;
        }

        private sealed class ManualTimer(
            ManualDeadlineTimerFactory factory,
            TimerCallback callback,
            object state,
            TimeSpan dueTime) : ITimer
        {
            private int _disposed;
            private TimeSpan _dueTime = dueTime;
            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            internal void Fire(bool includeDisposed)
            {
                if (!includeDisposed && IsDisposed)
                    return;
                callback(state);
            }

            public bool Change(TimeSpan nextDueTime, TimeSpan period)
            {
                _ = period;
                Interlocked.Increment(ref factory._changes);
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(ManualTimer));
                _dueTime = nextDueTime;
                return true;
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class EvidenceOwner : IPendingCallOwner
    {
        private readonly int _indexMask;
        private readonly TimeProvider _timeProvider;
        private readonly long[] _trackedRequestIds;
        private readonly long[] _dueTimestamps;
        private readonly LatenessHistogram _histogram = new();
        private long _registered;
        private long _completed;
        private long _deadlineCompletions;
        private long _normalCompletions;
        private long _missingDeadlineTracking;
        private long _producerCancellationFailures;

        internal EvidenceOwner(int capacity, TimeProvider timeProvider)
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
            var tracked = Interlocked.CompareExchange(ref _trackedRequestIds[index], 0, completion.RequestId) == completion.RequestId;
            var dueTimestamp = tracked ? Volatile.Read(ref _dueTimestamps[index]) : 0;
            if (tracked)
                Volatile.Write(ref _dueTimestamps[index], 0);

            if (completion.Reason == PendingCallCompletionReason.DeadlineExceeded)
            {
                if (!tracked || dueTimestamp == 0)
                    Interlocked.Increment(ref _missingDeadlineTracking);
                else
                    _histogram.RecordTimestampDelta(_timeProvider.GetTimestamp() - dueTimestamp, _timeProvider.TimestampFrequency);
                Interlocked.Increment(ref _deadlineCompletions);
            }
            else
            {
                Interlocked.Increment(ref _normalCompletions);
            }
            Interlocked.Increment(ref _completed);
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
            _ = exception;
            Interlocked.Increment(ref _producerCancellationFailures);
        }

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
                    throw new InvalidOperationException($"{phase} retained deadline tracking at slot {index}.");
            }
        }
    }

    private sealed class CountingOwner : IPendingCallOwner
    {
        private int _completed;
        internal int Completed => Volatile.Read(ref _completed);
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            _ = completion;
            Interlocked.Increment(ref _completed);
        }
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw exception;
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
                return maxBucket >= BucketCount ? MaxTrackedMilliseconds : maxBucket * BucketMicroseconds / 1000d;
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
                    return index >= BucketCount ? MaxTrackedMilliseconds : index * BucketMicroseconds / 1000d;
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
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { _ = completion; }
        public void OnProducerCancellationCallbackFailed(Exception exception) { _ = exception; }
    }

    private enum SchedulerKind { SharedScan, PerCallRuntimeTimer }
    private enum ExpiryPattern { Clustered, Staggered }
    private enum EvidencePriority { Primary, Boundary, Stress }

    private sealed record Scenario(
        string Name,
        EvidencePriority Priority,
        int Workers,
        int BatchSize,
        int Capacity,
        int DeadlineDensityBasisPoints,
        int ExpiryBasisPointsOfDeadlineCalls,
        int NormalDeadlineMilliseconds,
        int ExpiryDeadlineMilliseconds,
        ExpiryPattern ExpiryPattern,
        string DeadlineSource);

    private sealed record WorkerResult(long Completed, long NormalCompletions, long DeadlineCompletions);
    private sealed record TimerCounters(long Creates, long Changes, long Disposes, long Callbacks);
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
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double P95LatenessMilliseconds,
        double P99LatenessMilliseconds,
        double MaxLatenessMilliseconds,
        long LatenessOverflowCount);
    private sealed record RoundResult(
        string Scenario,
        string Priority,
        string DeadlineSource,
        string Scheduler,
        int Round,
        int Workers,
        int BatchSize,
        int Capacity,
        double DeadlineDensityPercent,
        double ExpiryPercentOfDeadlineCalls,
        string ExpiryPattern,
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
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long TimerCreates,
        long TimerChanges,
        long TimerDisposes,
        long TimerCallbacks,
        double TimerCallbacksPerSecond,
        double P95LatenessMilliseconds,
        double P99LatenessMilliseconds,
        double MaxLatenessMilliseconds,
        long LatenessOverflowCount);
    private sealed record ScenarioSummary(
        string Scenario,
        string Priority,
        string DeadlineSource,
        int Capacity,
        double DeadlineDensityPercent,
        double ExpiryPercentOfDeadlineCalls,
        double SharedMedianQps,
        double PerCallMedianQps,
        double PerCallQpsDeltaPercent,
        double SharedMedianCpuNanosecondsPerOperation,
        double PerCallMedianCpuNanosecondsPerOperation,
        double PerCallCpuDeltaPercent,
        double SharedMedianAllocatedBytesPerOperation,
        double PerCallMedianAllocatedBytesPerOperation,
        double PerCallAllocationDeltaBytesPerOperation,
        double SharedMedianTimerCallbacksPerSecond,
        double PerCallMedianTimerCallbacksPerSecond,
        double SharedMedianP95LatenessMilliseconds,
        double PerCallMedianP95LatenessMilliseconds,
        double SharedMedianP99LatenessMilliseconds,
        double PerCallMedianP99LatenessMilliseconds,
        double PerCallMedianTimerCreates,
        double PerCallMedianTimerDisposes);
    private sealed record ZeroDeadlineAllocationGuardrail(
        int Iterations,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        long RuntimeTimerCreates,
        long RuntimeTimerCallbacks);
    private sealed record ExecutionContextProbe(bool Captured, string? ObservedValue);
    private sealed record EvidenceReport(
        int SchemaVersion,
        string RuntimeVersion,
        int ProcessorCount,
        int PrimaryRounds,
        int BoundaryRounds,
        int WarmupSeconds,
        int DurationSeconds,
        string DeadlineScalingNote,
        ZeroDeadlineAllocationGuardrail ZeroDeadlineAllocationGuardrail,
        ExecutionContextProbe ExecutionContextProbe,
        RoundResult[] Results,
        ScenarioSummary[] Summaries);
    private sealed record CorrectnessCase(string Name, bool Passed, string? Error);
    private sealed record CorrectnessReport(int Total, int Failed, CorrectnessCase[] Cases);
    private sealed record CombinedReport(CorrectnessReport? Correctness, EvidenceReport? Evidence);
}
