using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// Reproducible PendingRequestTable occupancy/deadline/mixed-lifetime/recovery evidence for #571.
/// Timing values are evidence only. CI gates deterministic lifecycle/capacity/deadline invariants.
/// </summary>
internal static partial class PendingRequestMatrixEvidenceRunner
{
    private static readonly Exception CleanupException = new IOException("pending-request matrix cleanup");
    private static readonly Exception DisconnectException = new IOException("pending-request matrix disconnect");
    private static readonly byte[] ResponsePayload = new byte[sizeof(int)];
    private static readonly FieldInfo WaiterCountField = GetRequiredField("_waiterCount");
    private static readonly FieldInfo NextIdField = GetRequiredField("_nextId");
    private static readonly Action<PendingRequestTable> ScanExpiredDeadlines = GetDeadlineScanDelegate();

    public static async Task RunAsync(string[] args)
    {
        var tier = GetString(args, "--tier", "ci");
        if (tier is not ("ci" or "p0" or "p1"))
            throw new ArgumentOutOfRangeException(nameof(args), tier, "Tier must be ci, p0, or p1.");

        var output = GetString(args, "--output", "artifacts/perf/pending-request-matrix/report.json");
        var cells = new List<object>();

        RunStaleResponseGate(cells);
        await RunDisposeWaiterGateAsync(cells).ConfigureAwait(false);

        foreach (var cell in GetHighOccupancyCells(tier))
            RunHighOccupancyCell(cells, cell.Capacity, cell.OccupancyPercent, cell.Producers, cell.OperationsPerProducer);
        foreach (var capacity in GetFullCapacities(tier))
            RunFullFailFastCell(cells, capacity);

        foreach (var cell in GetSparseDeadlineCells(tier))
            RunSparseDeadlineCell(cells, cell.Capacity, cell.Active, cell.Deadlines, cell.Iterations, cell.Pattern);
        await RunRealTimerDeadlineCellAsync(cells, tier == "ci" ? 8 : 24).ConfigureAwait(false);

        foreach (var cell in GetLongShortCells(tier))
        {
            await RunLongShortCellAsync(
                    cells,
                    cell.Capacity,
                    cell.OccupancyPercent,
                    cell.LongPercent,
                    cell.Producers,
                    cell.TerminalMode,
                    cell.OperationsPerProducer)
                .ConfigureAwait(false);
        }

        await RunRecoveryCellAsync(
                cells,
                capacity: tier == "p1" ? 4096 : 1024,
                cycles: tier == "ci" ? 3 : tier == "p0" ? 7 : 20,
                waiterCount: tier == "p1" ? 32 : 8)
            .ConfigureAwait(false);

        await RunProductionProfileAsync(
                cells,
                name: "plain-control",
                tls: false,
                compression: false,
                metrics: false,
                retry: false,
                breaker: false,
                admission: false,
                traceAll: false,
                concurrency: tier == "ci" ? 4 : 8,
                operationsPerWorker: tier == "ci" ? 64 : 192)
            .ConfigureAwait(false);
        await RunProductionProfileAsync(
                cells,
                name: "typical-production",
                tls: true,
                compression: true,
                metrics: true,
                retry: true,
                breaker: true,
                admission: true,
                traceAll: false,
                concurrency: tier == "ci" ? 4 : 8,
                operationsPerWorker: tier == "ci" ? 64 : 192)
            .ConfigureAwait(false);
        if (tier == "p1")
        {
            await RunProductionProfileAsync(
                    cells,
                    name: "feature-heavy",
                    tls: true,
                    compression: true,
                    metrics: true,
                    retry: true,
                    breaker: true,
                    admission: true,
                    traceAll: true,
                    concurrency: 16,
                    operationsPerWorker: 192)
                .ConfigureAwait(false);
        }

        var report = new
        {
            phase = "complete",
            invariant = true,
            issue = 571,
            tier,
            commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            gc = GCSettings.IsServerGC ? "server" : "workstation",
            stopwatchFrequency = Stopwatch.Frequency,
            generatedAtUtc = DateTimeOffset.UtcNow,
            cellCount = cells.Count,
            cells,
            note = "Hosted-runner timing is evidence, not a pass/fail threshold. Deterministic correctness gates throw and fail the run."
        };

        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            phase = "complete",
            invariant = true,
            issue = 571,
            tier,
            cellCount = cells.Count,
            report = path
        }));
    }

    private static IEnumerable<OccupancyCell> GetHighOccupancyCells(string tier)
    {
        if (tier == "ci")
        {
            yield return new(64, 50, 1, 64);
            yield return new(64, 90, 8, 64);
            yield return new(64, 99, 8, 64);
            yield return new(1024, 90, 32, 64);
            yield return new(1024, 99, 32, 64);
            yield return new(65_536, 99, 32, 32);
            yield break;
        }

        foreach (var capacity in new[] { 64, 1024, 16_384, 65_536 })
        {
            foreach (var occupancy in new[] { 50, 75, 90, 95, 99 })
            {
                foreach (var producers in new[] { 1, 8, 32 })
                    yield return new(capacity, occupancy, producers, capacity <= 1024 ? 192 : 64);
            }
        }

        if (tier == "p1")
        {
            yield return new(16_384, 99, 128, 96);
            yield return new(65_536, 99, 128, 64);
        }
    }

    private static IEnumerable<int> GetFullCapacities(string tier)
        => tier switch
        {
            "ci" => [64, 1024],
            "p0" => [64, 1024, 16_384, 65_536],
            _ => [64, 1024, 16_384, 65_536]
        };

    private static IEnumerable<SparseDeadlineCell> GetSparseDeadlineCells(string tier)
    {
        if (tier == "ci")
        {
            yield return new(65_536, 8, 1, 256, "single");
            yield return new(65_536, 8, 2, 256, "staggered");
            yield break;
        }

        foreach (var capacity in new[] { 1024, 16_384, 65_536 })
        {
            foreach (var active in new[] { 1, 8, 32, 128 })
            {
                foreach (var ratio in new[] { 1, 10, 100 })
                {
                    var deadlines = Math.Clamp((int)Math.Ceiling(active * ratio / 100d), 1, active);
                    yield return new(capacity, active, deadlines, 512, ratio == 100 ? "clustered" : "staggered");
                }
            }
        }

        if (tier == "p1")
        {
            yield return new(
                SharpLinkProtocolOptions.MaximumPendingRequestsPerConnection,
                128,
                13,
                128,
                "clustered");
        }
    }

    private static IEnumerable<LongShortCell> GetLongShortCells(string tier)
    {
        var modes = tier == "ci"
            ? new[] { "response", "deadline", "disconnect" }
            : new[] { "response", "cancel", "deadline", "disconnect" };
        foreach (var mode in modes)
            yield return new(1024, 90, 10, 8, mode, tier == "ci" ? 64 : 192);

        if (tier == "p0")
            yield return new(16_384, 90, 10, 32, "response", 96);
        if (tier == "p1")
            yield return new(16_384, 99, 25, 128, "deadline", 96);
    }

    private static void RunStaleResponseGate(List<object> cells)
    {
        var owner = new RecordingOwner();
        using var table = CreateTable(64, TimeProvider.System, owner);
        var first = table.Rent<int>(out var staleId);
        CompleteSuccess(table, first, staleId);
        NextIdField.SetValue(table, staleId + table.Capacity - 1L);
        var second = table.Rent<int>(out var currentId);
        Require(currentId == staleId + table.Capacity,
            "Stale-response gate did not force reuse of the same physical slot.");
        var stalePayload = new ReadOnlySequence<byte>(ResponsePayload);
        Require(!table.Dispatch(staleId, ref stalePayload), "A stale response matched a newer request lifecycle.");
        Require(table.Contains(currentId), "The current lifecycle disappeared after a stale response.");
        CompleteSuccess(table, second, currentId);
        Require(table.ActiveCount == 0 && table.Count == 0, "Stale-response gate did not return to zero.");
        owner.RequireIdle();
        cells.Add(new
        {
            category = "hard-gate",
            scenario = "stale-response",
            capacity = 64,
            staleRequestId = staleId,
            currentRequestId = currentId,
            samePhysicalSlot = true,
            invariant = true
        });
    }

    private static async Task RunDisposeWaiterGateAsync(List<object> cells)
    {
        var owner = new RecordingOwner();
        var table = CreateTable(64, TimeProvider.System, owner);
        var held = Fill(table, 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = table.RentAsync<int>(true, default, timeout.Token).AsTask();
        Require(SpinWait.SpinUntil(() => GetWaiterCount(table) == 1, TimeSpan.FromSeconds(5)),
            "Dispose gate waiter never entered the capacity wait path.");
        table.Dispose();
        try
        {
            _ = await waiter.ConfigureAwait(false);
            throw new InvalidOperationException("Disposed pending table unexpectedly granted a waiter.");
        }
        catch (ObjectDisposedException)
        {
        }
        ObserveFailures(held.Select(static item => item.Operation), static exception =>
            exception is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed });
        Require(GetWaiterCount(table) == 0, "Dispose gate stranded a capacity waiter.");
        Require(table.ActiveCount == 0 && table.Count == 0, "Dispose gate stranded pending calls.");
        owner.RequireIdle();
        cells.Add(new
        {
            category = "hard-gate",
            scenario = "dispose-waiter-release",
            capacity = 64,
            waitersAfterDispose = GetWaiterCount(table),
            activeAfterDispose = table.ActiveCount,
            invariant = true
        });
    }

    private static void RunHighOccupancyCell(
        List<object> cells,
        int capacity,
        int occupancyPercent,
        int producers,
        int operationsPerProducer)
    {
        var target = Math.Clamp((int)Math.Floor(capacity * occupancyPercent / 100d), 1, capacity - 1);
        var owner = new RecordingOwner();
        using var table = CreateTable(capacity, TimeProvider.System, owner);
        var held = Fill(table, target);
        var workerCount = Math.Min(Math.Min(producers, target), 128);
        var currentOperations = new RpcRequestOperation<int>[workerCount];
        var currentIds = new long[workerCount];
        for (var worker = 0; worker < workerCount; worker++)
        {
            currentOperations[worker] = held[worker].Operation;
            currentIds[worker] = held[worker].Id;
        }

        var workerLatencies = new long[workerCount][];
        var workerOccupancies = new int[workerCount][];
        var workerElapsed = new long[workerCount];
        using var start = new ManualResetEventSlim(false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var nextIdBefore = GetNextId(table);
        var wallStarted = Stopwatch.GetTimestamp();
        var tasks = new Task[workerCount];
        for (var worker = 0; worker < workerCount; worker++)
        {
            var workerIndex = worker;
            tasks[worker] = Task.Run(() =>
            {
                var latencies = new long[operationsPerProducer];
                var occupancies = new int[operationsPerProducer * 2];
                workerLatencies[workerIndex] = latencies;
                workerOccupancies[workerIndex] = occupancies;
                start.Wait();
                var workerStarted = Stopwatch.GetTimestamp();
                for (var iteration = 0; iteration < operationsPerProducer; iteration++)
                {
                    var operationStarted = Stopwatch.GetTimestamp();
                    CompleteSuccess(table, currentOperations[workerIndex], currentIds[workerIndex]);
                    occupancies[iteration * 2] = table.ActiveCount;
                    currentOperations[workerIndex] = table.Rent<int>(out currentIds[workerIndex]);
                    occupancies[iteration * 2 + 1] = table.ActiveCount;
                    latencies[iteration] = Stopwatch.GetTimestamp() - operationStarted;
                }
                workerElapsed[workerIndex] = Stopwatch.GetTimestamp() - workerStarted;
            });
        }
        start.Set();
        Task.WaitAll(tasks);
        var wallElapsed = Stopwatch.GetTimestamp() - wallStarted;
        var nextIdAfter = GetNextId(table);
        process.Refresh();
        var cpuMilliseconds = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds - cpuBefore);
        var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
        var registrations = (long)workerCount * operationsPerProducer;
        var requestIdAdvances = nextIdAfter - nextIdBefore;
        Require(requestIdAdvances >= registrations, "Request IDs advanced fewer times than successful registrations.");
        Require(table.ActiveCount == target && table.Count == target,
            "High-occupancy cell did not preserve the requested steady-state occupancy.");

        var ticks = workerLatencies.SelectMany(static values => values).ToArray();
        var occupanciesAll = workerOccupancies.SelectMany(static values => values).ToArray();
        Require(occupanciesAll.Length != 0 && occupanciesAll.Max() <= capacity, "Pending occupancy exceeded capacity.");
        var perWorkerNs = workerElapsed.Select(value => ToNanoseconds(value) / operationsPerProducer).ToArray();
        var progress = Enumerable.Repeat(operationsPerProducer, workerCount).ToArray();

        var cleanupOperations = new List<RpcRequestOperation<int>>(target);
        cleanupOperations.AddRange(currentOperations);
        for (var index = workerCount; index < held.Length; index++)
            cleanupOperations.Add(held[index].Operation);
        table.FailAllPendingRequests(CleanupException);
        ObserveFailures(cleanupOperations, static exception => ReferenceEquals(exception, CleanupException));
        Require(table.ActiveCount == 0 && table.Count == 0, "High-occupancy cleanup stranded pending calls.");
        owner.RequireIdle();

        cells.Add(new
        {
            category = "high-occupancy",
            capacity,
            requestedOccupancyPercent = occupancyPercent,
            targetOccupancy = target,
            actualOccupancyPercent = target * 100d / capacity,
            producers = workerCount,
            operations = registrations,
            qps = registrations / Math.Max(0.000001, ToSeconds(wallElapsed)),
            cpuNanosecondsPerOperation = cpuMilliseconds * 1_000_000d / registrations,
            allocatedBytesPerOperation = allocatedBytes / (double)registrations,
            latencyNanoseconds = TimingStatistics(ticks),
            occupancy = Statistics(occupanciesAll.Select(static value => (long)value).ToArray()),
            requestIdAdvances,
            extraProbeAttempts = requestIdAdvances - registrations,
            resourceExhausted = 0,
            perProducerProgressMin = progress.Min(),
            perProducerProgressMax = progress.Max(),
            perProducerNanosecondsPerOperation = Statistics(perWorkerNs.Select(static value => (long)value).ToArray()),
            invariant = true
        });
    }

    private static void RunFullFailFastCell(List<object> cells, int capacity)
    {
        var owner = new RecordingOwner();
        using var table = CreateTable(capacity, TimeProvider.System, owner);
        var held = Fill(table, capacity);
        const int attempts = 64;
        var latencies = new long[attempts];
        var rejected = 0;
        for (var index = 0; index < attempts; index++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                _ = table.Rent<int>(out _);
                throw new InvalidOperationException("A full table accepted a fail-fast registration.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                rejected++;
                latencies[index] = Stopwatch.GetTimestamp() - started;
            }
        }
        Require(rejected == attempts, "Full table did not reject every fail-fast registration.");
        Require(table.ActiveCount == capacity && table.Count == capacity, "Fail-fast rejection corrupted capacity accounting.");

        CompleteSuccess(table, held[0].Operation, held[0].Id);
        var replacement = table.Rent<int>(out var replacementId);
        Require(table.ActiveCount == capacity, "Released full-table capacity could not be reused immediately.");
        CompleteSuccess(table, replacement, replacementId);
        var remaining = held.Skip(1).Select(static item => item.Operation).ToArray();
        table.FailAllPendingRequests(CleanupException);
        ObserveFailures(remaining, static exception => ReferenceEquals(exception, CleanupException));
        Require(table.ActiveCount == 0 && table.Count == 0, "Full-table cleanup did not return to zero.");
        owner.RequireIdle();

        cells.Add(new
        {
            category = "high-occupancy",
            scenario = "full-fail-fast",
            capacity,
            requestedOccupancyPercent = 100,
            targetOccupancy = capacity,
            actualOccupancyPercent = 100d,
            attempts,
            resourceExhausted = rejected,
            failFastNanoseconds = TimingStatistics(latencies),
            fullCapacityReusable = true,
            invariant = true
        });
    }

    private static void RunSparseDeadlineCell(
        List<object> cells,
        int capacity,
        int active,
        int deadlines,
        int iterations,
        string pattern)
    {
        Require(deadlines > 0 && deadlines <= active, "Sparse deadline cell has invalid deadline count.");
        var time = new AdvancingTimeProvider();
        var owner = new RecordingOwner();
        using var table = CreateTable(capacity, time, owner);
        var operations = new RpcRequestOperation<int>[active];
        var ids = new long[active];
        for (var index = 0; index < active; index++)
        {
            if (index < deadlines)
            {
                var offset = pattern == "staggered" ? 100 + index * 10 : 100;
                var deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(offset), time);
                operations[index] = table.Rent(
                    Int32Codec.Instance,
                    PendingCallKind.Unary,
                    deadline,
                    CancellationToken.None,
                    out ids[index]);
            }
            else
            {
                operations[index] = table.Rent<int>(out ids[index]);
            }
        }

        for (var warmup = 0; warmup < 16; warmup++)
            ScanExpiredDeadlines(table);
        var started = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
            ScanExpiredDeadlines(table);
        var elapsed = Stopwatch.GetTimestamp() - started;

        for (var index = 0; index < deadlines; index++)
            Require(!operations[index].AsValueTask().IsCompleted, "Deadline completed before its monotonic boundary.");
        time.Advance(TimeSpan.FromMilliseconds(pattern == "staggered" ? 100 + (deadlines - 1) * 10 : 100));
        ScanExpiredDeadlines(table);
        for (var index = 0; index < deadlines; index++)
            ObserveDeadlineFailure(operations[index]);

        var remaining = operations.Skip(deadlines).ToArray();
        table.FailAllPendingRequests(CleanupException);
        ObserveFailures(remaining, static exception => ReferenceEquals(exception, CleanupException));
        Require(table.ActiveCount == 0 && table.Count == 0, "Sparse-deadline cell stranded pending calls.");
        owner.RequireIdle();

        cells.Add(new
        {
            category = "sparse-deadline",
            capacity,
            active,
            deadlineCalls = deadlines,
            deadlinePercent = deadlines * 100d / active,
            pattern,
            iterations,
            inspectedSlotsPerScan = capacity,
            inspectedActiveCallsPerScan = active,
            nanosecondsPerScan = ToNanoseconds(elapsed) / iterations,
            deadlineNeverEarly = true,
            expiredAtBoundary = deadlines,
            invariant = true
        });
    }

    private static async Task RunRealTimerDeadlineCellAsync(List<object> cells, int samples)
    {
        var owner = new RecordingOwner();
        using var table = CreateTable(65_536, TimeProvider.System, owner);
        var fillers = Fill(table, 7);
        var lateness = new double[samples];
        const int deadlineMilliseconds = 15;
        for (var index = 0; index < samples; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var operation = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.Unary,
                RpcDeadline.Create(TimeSpan.FromMilliseconds(deadlineMilliseconds), TimeProvider.System),
                CancellationToken.None,
                out _);
            try
            {
                _ = await operation.AsValueTask().ConfigureAwait(false);
                throw new InvalidOperationException("Real timer deadline completed successfully.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
            {
            }
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lateness[index] = Math.Max(0, elapsedMilliseconds - deadlineMilliseconds);
        }
        table.FailAllPendingRequests(CleanupException);
        ObserveFailures(fillers.Select(static item => item.Operation), static exception => ReferenceEquals(exception, CleanupException));
        owner.RequireIdle();
        Array.Sort(lateness);
        cells.Add(new
        {
            category = "sparse-deadline",
            scenario = "real-timer-combined",
            capacity = 65_536,
            activeNonDeadline = 7,
            samples,
            deadlineMilliseconds,
            p50LatenessMilliseconds = Percentile(lateness, 0.50),
            p95LatenessMilliseconds = Percentile(lateness, 0.95),
            p99LatenessMilliseconds = Percentile(lateness, 0.99),
            maxLatenessMilliseconds = lateness[^1],
            invariant = true
        });
    }

    private static async Task RunLongShortCellAsync(
        List<object> cells,
        int capacity,
        int occupancyPercent,
        int longPercent,
        int producers,
        string terminalMode,
        int operationsPerProducer)
    {
        var target = Math.Clamp((int)Math.Floor(capacity * occupancyPercent / 100d), 2, capacity - 1);
        var longCount = Math.Clamp((int)Math.Ceiling(target * longPercent / 100d), 1, target - 1);
        var time = terminalMode == "deadline" ? new AdvancingTimeProvider() : null;
        var provider = (TimeProvider?)time ?? TimeProvider.System;
        var owner = new RecordingOwner();
        using var table = CreateTable(capacity, provider, owner);
        using var cancellation = terminalMode == "cancel" ? new CancellationTokenSource() : null;
        var longOperations = new RpcRequestOperation<int>[longCount];
        var longIds = new long[longCount];
        for (var index = 0; index < longCount; index++)
        {
            var deadline = terminalMode == "deadline"
                ? RpcDeadline.Create(TimeSpan.FromMilliseconds(250), provider)
                : default;
            longOperations[index] = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.Unary,
                deadline,
                cancellation?.Token ?? CancellationToken.None,
                out longIds[index]);
        }

        var shortCount = target - longCount;
        var shortHeld = Fill(table, shortCount);
        var workerCount = Math.Min(Math.Min(producers, shortCount), 128);
        var currentOperations = new RpcRequestOperation<int>[workerCount];
        var currentIds = new long[workerCount];
        for (var worker = 0; worker < workerCount; worker++)
        {
            currentOperations[worker] = shortHeld[worker].Operation;
            currentIds[worker] = shortHeld[worker].Id;
        }

        var latencyByWorker = new long[workerCount][];
        var occupancyByWorker = new int[workerCount][];
        using var start = new ManualResetEventSlim(false);
        var tasks = new Task[workerCount];
        var wallStarted = Stopwatch.GetTimestamp();
        for (var worker = 0; worker < workerCount; worker++)
        {
            var workerIndex = worker;
            tasks[worker] = Task.Run(() =>
            {
                var latencies = new long[operationsPerProducer];
                var occupancies = new int[operationsPerProducer * 2];
                latencyByWorker[workerIndex] = latencies;
                occupancyByWorker[workerIndex] = occupancies;
                start.Wait();
                for (var iteration = 0; iteration < operationsPerProducer; iteration++)
                {
                    var operationStarted = Stopwatch.GetTimestamp();
                    CompleteSuccess(table, currentOperations[workerIndex], currentIds[workerIndex]);
                    occupancies[iteration * 2] = table.ActiveCount;
                    currentOperations[workerIndex] = table.Rent<int>(out currentIds[workerIndex]);
                    occupancies[iteration * 2 + 1] = table.ActiveCount;
                    latencies[iteration] = Stopwatch.GetTimestamp() - operationStarted;
                }
            });
        }
        start.Set();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var shortElapsed = Stopwatch.GetTimestamp() - wallStarted;
        Require(table.ActiveCount == target && table.Count == target, "Long/short steady-state occupancy drifted.");

        var terminalStarted = Stopwatch.GetTimestamp();
        switch (terminalMode)
        {
            case "response":
                for (var index = 0; index < longOperations.Length; index++)
                    CompleteSuccess(table, longOperations[index], longIds[index]);
                break;
            case "cancel":
                cancellation!.Cancel();
                ObserveFailures(longOperations, static exception => exception is OperationCanceledException);
                break;
            case "deadline":
                foreach (var operation in longOperations)
                    Require(!operation.AsValueTask().IsCompleted, "Long deadline completed before the controlled boundary.");
                time!.Advance(TimeSpan.FromMilliseconds(250));
                ScanExpiredDeadlines(table);
                foreach (var operation in longOperations)
                    ObserveDeadlineFailure(operation);
                break;
            case "disconnect":
                table.FailAllPendingRequests(DisconnectException);
                ObserveFailures(longOperations, static exception => ReferenceEquals(exception, DisconnectException));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(terminalMode));
        }
        var terminalElapsed = Stopwatch.GetTimestamp() - terminalStarted;

        var remainingShort = new List<RpcRequestOperation<int>>(shortCount);
        remainingShort.AddRange(currentOperations);
        for (var index = workerCount; index < shortHeld.Length; index++)
            remainingShort.Add(shortHeld[index].Operation);
        if (terminalMode == "disconnect")
            ObserveFailures(remainingShort, static exception => ReferenceEquals(exception, DisconnectException));
        else
        {
            table.FailAllPendingRequests(CleanupException);
            ObserveFailures(remainingShort, static exception => ReferenceEquals(exception, CleanupException));
        }
        Require(table.ActiveCount == 0 && table.Count == 0, "Long/short cleanup stranded pending calls.");
        owner.RequireIdle();

        var shortLatencies = latencyByWorker.SelectMany(static values => values).ToArray();
        var occupancies = occupancyByWorker.SelectMany(static values => values).ToArray();
        Require(occupancies.Max() <= capacity, "Long/short occupancy exceeded capacity.");
        var operations = (long)workerCount * operationsPerProducer;
        cells.Add(new
        {
            category = "long-short-mix",
            capacity,
            requestedOccupancyPercent = occupancyPercent,
            targetOccupancy = target,
            actualOccupancyPercent = target * 100d / capacity,
            longPercent,
            longCalls = longCount,
            producers = workerCount,
            terminalMode,
            shortOperations = operations,
            shortQps = operations / Math.Max(0.000001, ToSeconds(shortElapsed)),
            shortLatencyNanoseconds = TimingStatistics(shortLatencies),
            occupancy = Statistics(occupancies.Select(static value => (long)value).ToArray()),
            longTerminalMilliseconds = Stopwatch.GetElapsedTime(0, terminalElapsed).TotalMilliseconds,
            perProducerProgressMin = operationsPerProducer,
            perProducerProgressMax = operationsPerProducer,
            invariant = true
        });
    }

    private static async Task RunRecoveryCellAsync(
        List<object> cells,
        int capacity,
        int cycles,
        int waiterCount)
    {
        var owner = new RecordingOwner();
        using var table = CreateTable(capacity, TimeProvider.System, owner);
        var cycleEvidence = new List<object>(cycles);
        var heapAfterGc = new long[cycles];
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var baseline = MeasureSequentialProbe(table, 64);
            var held = Fill(table, capacity);
            Require(table.ActiveCount == capacity, "Recovery phase C did not reach full occupancy.");

            var rejected = 0;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                try
                {
                    _ = table.Rent<int>(out _);
                }
                catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    rejected++;
                }
            }
            Require(rejected == 16, "Recovery overload phase did not fail fast at full capacity.");

            using var waiterTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var waitStarted = Stopwatch.GetTimestamp();
            var waiterTasks = new Task<PendingRequestLease<int>>[waiterCount];
            for (var index = 0; index < waiterTasks.Length; index++)
            {
                waiterTasks[index] = table
                    .RentAsync<int>(true, default, waiterTimeout.Token)
                    .AsTask();
            }
            Require(SpinWait.SpinUntil(() => GetWaiterCount(table) == waiterCount, TimeSpan.FromSeconds(5)),
                "Recovery overload waiters did not all enter the capacity wait path.");

            for (var index = 0; index < waiterCount; index++)
                CompleteSuccess(table, held[index].Operation, held[index].Id);
            var waiterLeases = await Task.WhenAll(waiterTasks).ConfigureAwait(false);
            var waitElapsed = Stopwatch.GetTimestamp() - waitStarted;
            Require(GetWaiterCount(table) == 0, "Recovery left a capacity waiter after release.");
            foreach (var lease in waiterLeases)
                CompleteSuccess(table, lease.Operation, lease.Id);

            var remaining = held.Skip(waiterCount).Select(static item => item.Operation).ToArray();
            table.FailAllPendingRequests(DisconnectException);
            ObserveFailures(remaining, static exception => ReferenceEquals(exception, DisconnectException));
            Require(table.ActiveCount == 0 && table.Count == 0, "Recovery phase D did not return pending state to zero.");
            Require(GetWaiterCount(table) == 0, "Recovery phase D stranded waiters.");

            var reuse = Fill(table, capacity);
            Require(table.ActiveCount == capacity && table.Count == capacity, "Recovery did not restore full reusable capacity.");
            table.FailAllPendingRequests(CleanupException);
            ObserveFailures(reuse.Select(static item => item.Operation), static exception => ReferenceEquals(exception, CleanupException));
            Require(table.ActiveCount == 0 && table.Count == 0, "Reusable-capacity proof did not return to zero.");
            var recovered = MeasureSequentialProbe(table, 64);

            ForceFullGc();
            heapAfterGc[cycle] = GC.GetTotalMemory(forceFullCollection: false);
            cycleEvidence.Add(new
            {
                cycle = cycle + 1,
                baselineP99Nanoseconds = baseline.P99,
                recoveredP99Nanoseconds = recovered.P99,
                rejected,
                waiters = waiterCount,
                waiterReleaseMilliseconds = Stopwatch.GetElapsedTime(0, waitElapsed).TotalMilliseconds,
                activeAfterRecovery = table.ActiveCount,
                waitersAfterRecovery = GetWaiterCount(table),
                heapAfterFullGcBytes = heapAfterGc[cycle],
                fullCapacityReusable = true
            });
        }

        owner.RequireIdle();
        var minHeap = heapAfterGc.Min();
        var maxHeap = heapAfterGc.Max();
        Require(maxHeap - minHeap < 64L * 1024 * 1024,
            "Repeated overload/recovery retained more than 64 MiB above its minimum full-GC heap.");
        cells.Add(new
        {
            category = "overload-recovery",
            capacity,
            cycles,
            waiterCount,
            cycleEvidence,
            minHeapAfterFullGcBytes = minHeap,
            maxHeapAfterFullGcBytes = maxHeap,
            retainedHeapRangeBytes = maxHeap - minHeap,
            activeAfterAllCycles = table.ActiveCount,
            waitersAfterAllCycles = GetWaiterCount(table),
            fullCapacityReusable = true,
            invariant = true
        });
    }
}
