using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class AdmissionPartitionRpcEvidence
{
    private static readonly JsonSerializerOptions SJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var concurrency) || concurrency <= 0)
        {
            throw new ArgumentException(
                "Usage: --issue-245-rpc-evidence <label> <concurrency> <output-json>");
        }

        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 256), completionPortThreads);

        var result = await RunRpcAsync(args[0], concurrency).ConfigureAwait(false);
        var outputPath = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(result, SJsonOptions)).ConfigureAwait(false);

        Console.WriteLine(
            $"ISSUE245_RPC label={result.Label} concurrency={result.Concurrency} " +
            $"ops={result.Operations} qps={result.ThroughputPerSecond:F0} " +
            $"p99_us={result.P99Us:F3} cpu_us_op={result.CpuUsPerOperation:F3} " +
            $"alloc_b_op={result.AllocatedBytesPerOperation:F1}");
    }

    private static async Task<AdmissionPartitionRpcEvidenceResult> RunRpcAsync(
        string label,
        int concurrency)
    {
        const int partitions = 1024;
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

        // Every invocation is a fresh process. Equalize both call-count warmup and
        // wall-clock maturation so a faster candidate does not enter measurement
        // before Tiered PGO/background JIT work has had the same opportunity to settle.
        const int warmupOperations = 20_000;
        var warmupStarted = Stopwatch.GetTimestamp();
        for (var operation = 0; operation < warmupOperations; operation++)
        {
            var result = await environment.Rpc.AddAsync(10, 20).ConfigureAwait(false);
            if (result != 30)
                throw new InvalidOperationException($"RPC warmup returned {result} instead of 30.");
        }

        var warmupElapsed = Stopwatch.GetElapsedTime(warmupStarted);
        var remainingWarmup = TimeSpan.FromSeconds(5) - warmupElapsed;
        if (remainingWarmup > TimeSpan.Zero)
            await Task.Delay(remainingWarmup).ConfigureAwait(false);

        Interlocked.Exchange(ref selectorIndex, -1);
        await Task.Delay(100).ConfigureAwait(false);

        var operationsPerWorker = concurrency switch
        {
            1 => 50_000,
            32 => 2_000,
            128 => 500,
            _ => Math.Max(500, 64_000 / concurrency)
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
        var rank = Math.Clamp(
            (int)Math.Ceiling(0.99d * latencies.Length) - 1,
            0,
            latencies.Length - 1);

        return new AdmissionPartitionRpcEvidenceResult
        {
            Label = label,
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Concurrency = concurrency,
            Operations = operationCount,
            ThroughputPerSecond = operationCount / elapsed.TotalSeconds,
            P99Us = latencies[rank] * 1_000_000d / Stopwatch.Frequency,
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / operationCount,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)operationCount
        };
    }
}

internal sealed class AdmissionPartitionRpcEvidenceResult
{
    public string Label { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public int Operations { get; init; }
    public double ThroughputPerSecond { get; init; }
    public double P99Us { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
}
