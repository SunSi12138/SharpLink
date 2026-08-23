using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class AdmissionPartitionReviewEvidence
{
    private static readonly JsonSerializerOptions SJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --issue-245-review-evidence <label> <output-json>");
        }

        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 256), completionPortThreads);

        var label = args[0];
        var outputPath = Path.GetFullPath(args[1]);
        var results = new List<AdmissionPartitionEvidenceResult>();

        foreach (var partitions in new[] { 1, 128, 1024 })
        {
            foreach (var concurrency in new[] { 1, 32, 128 })
            {
                results.Add(await RunPoolAsync(
                    label,
                    "pool-recently-idle",
                    partitions,
                    concurrency,
                    expiredChurn: false).ConfigureAwait(false));
            }
        }

        foreach (var partitions in new[] { 128, 1024 })
        {
            foreach (var concurrency in new[] { 32, 128 })
            {
                results.Add(await RunPoolAsync(
                    label,
                    "pool-expired-churn",
                    partitions,
                    concurrency,
                    expiredChurn: true).ConfigureAwait(false));
            }
        }

        foreach (var concurrency in new[] { 1, 32, 128 })
        {
            results.Add(await RunRpcAsync(label, partitions: 1024, concurrency)
                .ConfigureAwait(false));
        }

        var document = new AdmissionPartitionEvidenceDocument
        {
            Label = label,
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            TimestampUtc = DateTimeOffset.UtcNow,
            Results = results
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(document, SJsonOptions)).ConfigureAwait(false);

        foreach (var result in results)
        {
            Console.WriteLine(
                $"ISSUE245_EVIDENCE label={result.Label} kind={result.Kind} " +
                $"partitions={result.Partitions} concurrency={result.Concurrency} " +
                $"ops={result.Operations} qps={result.ThroughputPerSecond:F0} " +
                $"p99_us={result.P99Us:F3} cpu_us_op={result.CpuUsPerOperation:F3} " +
                $"alloc_b_op={result.AllocatedBytesPerOperation:F1} " +
                $"scans={result.ReclaimScans} visited={result.ReclaimEntriesVisited}");
        }
    }

    private static async Task<AdmissionPartitionEvidenceResult> RunPoolAsync(
        string label,
        string kind,
        int partitions,
        int concurrency,
        bool expiredChurn)
    {
        var timeout = expiredChurn ? TimeSpan.FromTicks(10) : TimeSpan.FromMinutes(5);
        var time = new ManualTimeProvider();
        var options = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = partitions,
            IdleTimeout = timeout
        };
        options.UseConcurrency(1);

        var contexts = Enumerable.Range(0, partitions)
            .Select(index => new SharpLinkAdmissionContext(
                1,
                2,
                RpcMethodKind.Unary,
                $"partition-{index}",
                null,
                null,
                null))
            .ToArray();

        using var pool = new AdmissionPartitionPool(
            static context => context.ConnectionId,
            options,
            queueLimit: 0,
            time);

        foreach (var context in contexts)
            pool.TryAcquire(context)!.Dispose();

        var operationsPerWorker = concurrency switch
        {
            1 => expiredChurn ? 5_000 : 20_000,
            32 => expiredChurn ? 750 : 2_000,
            _ => expiredChurn ? 250 : 500
        };
        var operationCount = checked(concurrency * operationsPerWorker);
        var latencies = new long[operationCount];
        using var startGate = new ManualResetEventSlim(false);
        var tasks = new Task[concurrency];

        var reclaimScansBefore = ReadCounter(pool, "ReclaimScanCount");
        var reclaimVisitedBefore = ReadCounter(pool, "ReclaimEntriesVisited");

        for (var worker = 0; worker < concurrency; worker++)
        {
            var workerIndex = worker;
            tasks[worker] = Task.Run(() =>
            {
                startGate.Wait();
                var offset = workerIndex * operationsPerWorker;
                for (var operation = 0; operation < operationsPerWorker; operation++)
                {
                    if (expiredChurn && workerIndex == 0 && operation != 0 && operation % 32 == 0)
                        time.Advance(timeout + TimeSpan.FromTicks(1));

                    var context = contexts[(workerIndex * 17 + operation) % contexts.Length];
                    var started = Stopwatch.GetTimestamp();
                    var lease = pool.TryAcquire(context)
                        ?? throw new InvalidOperationException("partition capacity unexpectedly rejected evidence operation");
                    lease.Dispose();
                    latencies[offset + operation] = Stopwatch.GetTimestamp() - started;
                }
            });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAll = Stopwatch.GetTimestamp();
        startGate.Set();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAll);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        Array.Sort(latencies);
        var reclaimScansAfter = ReadCounter(pool, "ReclaimScanCount");
        var reclaimVisitedAfter = ReadCounter(pool, "ReclaimEntriesVisited");

        return new AdmissionPartitionEvidenceResult
        {
            Label = label,
            Kind = kind,
            Partitions = partitions,
            Concurrency = concurrency,
            Operations = operationCount,
            ThroughputPerSecond = operationCount / elapsed.TotalSeconds,
            P99Us = PercentileUs(latencies, 99),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / operationCount,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)operationCount,
            ReclaimScans = CounterDelta(reclaimScansBefore, reclaimScansAfter),
            ReclaimEntriesVisited = CounterDelta(reclaimVisitedBefore, reclaimVisitedAfter)
        };
    }

    private static async Task<AdmissionPartitionEvidenceResult> RunRpcAsync(
        string label,
        int partitions,
        int concurrency)
    {
        var keys = Enumerable.Range(0, partitions)
            .Select(static index => $"partition-{index}")
            .ToArray();
        var selectorIndex = -1;

        await using var environment = await BenchmarkEnvironment.CreateAsync(
            configureServer: builder => builder.UseAdmissionControl(options =>
                options.UsePartition(
                    _ => keys[(Interlocked.Increment(ref selectorIndex) & int.MaxValue) % keys.Length],
                    partition =>
                    {
                        partition.MaxPartitions = partitions;
                        partition.IdleTimeout = TimeSpan.FromMinutes(5);
                        partition.UseConcurrency(4096);
                    }))).ConfigureAwait(false);

        for (var operation = 0; operation < partitions * 2; operation++)
        {
            var result = await environment.Rpc.AddAsync(10, 20).ConfigureAwait(false);
            if (result != 30)
                throw new InvalidOperationException($"RPC warmup returned {result} instead of 30.");
        }
        Interlocked.Exchange(ref selectorIndex, -1);

        var operationsPerWorker = concurrency switch
        {
            1 => 5_000,
            32 => 500,
            _ => 150
        };
        var operationCount = checked(concurrency * operationsPerWorker);
        var latencies = new long[operationCount];
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[concurrency];

        for (var worker = 0; worker < concurrency; worker++)
        {
            var workerIndex = worker;
            tasks[worker] = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                var offset = workerIndex * operationsPerWorker;
                for (var operation = 0; operation < operationsPerWorker; operation++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = await environment.Rpc.AddAsync(10, 20).ConfigureAwait(false);
                    latencies[offset + operation] = Stopwatch.GetTimestamp() - started;
                    if (result != 30)
                        throw new InvalidOperationException($"RPC measurement returned {result} instead of 30.");
                }
            });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAll = Stopwatch.GetTimestamp();
        gate.SetResult(true);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAll);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        Array.Sort(latencies);
        return new AdmissionPartitionEvidenceResult
        {
            Label = label,
            Kind = "rpc-recently-idle",
            Partitions = partitions,
            Concurrency = concurrency,
            Operations = operationCount,
            ThroughputPerSecond = operationCount / elapsed.TotalSeconds,
            P99Us = PercentileUs(latencies, 99),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / operationCount,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)operationCount,
            ReclaimScans = -1,
            ReclaimEntriesVisited = -1
        };
    }

    private static long ReadCounter(AdmissionPartitionPool pool, string propertyName)
    {
        var property = typeof(AdmissionPartitionPool).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return property?.GetValue(pool) is long value ? value : -1;
    }

    private static long CounterDelta(long before, long after)
        => before < 0 || after < 0 ? -1 : after - before;

    private static double PercentileUs(long[] sortedTicks, double percentile)
    {
        var rank = Math.Clamp(
            (int)Math.Ceiling(percentile / 100d * sortedTicks.Length) - 1,
            0,
            sortedTicks.Length - 1);
        return sortedTicks[rank] * 1_000_000d / Stopwatch.Frequency;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void Advance(TimeSpan amount)
            => Interlocked.Add(ref _timestamp, amount.Ticks);
    }
}

internal sealed class AdmissionPartitionEvidenceDocument
{
    public string Label { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; }
    public List<AdmissionPartitionEvidenceResult> Results { get; init; } = [];
}

internal sealed class AdmissionPartitionEvidenceResult
{
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int Partitions { get; init; }
    public int Concurrency { get; init; }
    public int Operations { get; init; }
    public double ThroughputPerSecond { get; init; }
    public double P99Us { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public long ReclaimScans { get; init; }
    public long ReclaimEntriesVisited { get; init; }
}
