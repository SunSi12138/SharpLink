using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.Benchmarks;

/// <summary>
/// Equal-source, full-RPC #741 baseline/candidate evidence. All scenarios use generated
/// Idempotent AddAsync over a real transport. The retry controls verify physical attempt counts.
/// </summary>
internal static class Issue741RpcEvidenceRunner
{
    private const int MaxRecordedOperations = 2_000_000;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException(
                "Usage: --issue741-rpc-evidence <plain|retry-first|retry-once|retry-backoff|retry-exhausted> " +
                "<tcp|sharedmemory> <concurrency> <warmup-ms> <measure-ms> <output-json>");

        var scenario = args[0].ToLowerInvariant();
        var transport = args[1].ToLowerInvariant();
        var concurrency = int.Parse(args[2], CultureInfo.InvariantCulture);
        var warmupMs = int.Parse(args[3], CultureInfo.InvariantCulture);
        var measureMs = int.Parse(args[4], CultureInfo.InvariantCulture);
        var output = Path.GetFullPath(args[5]);

        if (scenario is not ("plain" or "retry-first" or "retry-once" or "retry-backoff" or "retry-exhausted"))
            throw new ArgumentOutOfRangeException(nameof(scenario));
        if (transport is not ("tcp" or "sharedmemory"))
            throw new ArgumentOutOfRangeException(nameof(transport));
        if (concurrency is < 1 or > 512 || warmupMs < 0 || measureMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(args));

        var service = new Issue741RetryProbeService(scenario);
        await using var benchmark = transport == "tcp"
            ? await BenchmarkEnvironment.CreateAsync(
                createClientBuilder: port => CreateClient(
                    SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(), port), scenario),
                customRpcService: service)
                .ConfigureAwait(false)
            : await BenchmarkEnvironment.CreateSharedMemoryAsync(
                createClientBuilder: name => CreateClient(
                    SharpClientBuilder.Create().UseSharedMemory(name)
                        .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), scenario),
                customRpcService: service)
                .ConfigureAwait(false);

        var nextCallId = 0;
        var warmupUntil = Stopwatch.GetTimestamp() +
            (long)(warmupMs / 1000.0 * Stopwatch.Frequency);
        do
        {
            await InvokeCheckedAsync(benchmark.Rpc, scenario, Interlocked.Increment(ref nextCallId))
                .ConfigureAwait(false);
        } while (Stopwatch.GetTimestamp() < warmupUntil);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var latencies = new long[MaxRecordedOperations];
        var completed = 0;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var attemptsBefore = service.Attempts;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[concurrency];
        var measurementStarted = Stopwatch.GetTimestamp();
        var measurementDeadline = measurementStarted +
            (long)(measureMs / 1000.0 * Stopwatch.Frequency);

        for (var i = 0; i < workers.Length; i++)
            workers[i] = WorkerAsync();
        startGate.TrySetResult();
        await Task.WhenAll(workers).ConfigureAwait(false);

        var measurementElapsed = Stopwatch.GetElapsedTime(measurementStarted);
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var attempts = service.Attempts - attemptsBefore;
        var expectedAttemptsPerCall = scenario == "retry-exhausted" ? 3 :
            scenario is "retry-once" or "retry-backoff" ? 2 : 1;
        if (completed == 0 || attempts != completed * expectedAttemptsPerCall)
        {
            throw new InvalidOperationException(
                $"Invalid measured workload: calls={completed}, physicalAttempts={attempts}, " +
                $"expectedAttemptsPerCall={expectedAttemptsPerCall}.");
        }

        Array.Sort(latencies, 0, completed);
        var result = new Issue741RpcEvidenceResult
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            RuntimeMode = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_MODE") ?? "jit",
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            RuntimeArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CpuCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            Scenario = scenario,
            Transport = transport,
            Concurrency = concurrency,
            WarmupMilliseconds = warmupMs,
            MeasurementMilliseconds = measureMs,
            ActualMeasurementSeconds = measurementElapsed.TotalSeconds,
            Operations = completed,
            PhysicalAttempts = attempts,
            ExpectedAttemptsPerCall = expectedAttemptsPerCall,
            ValidationFailures = 0,
            ThroughputOperationsPerSecond = completed / measurementElapsed.TotalSeconds,
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)completed,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            P50Us = Percentile(latencies, completed, 50),
            P99Us = Percentile(latencies, completed, 99),
            P999Us = Percentile(latencies, completed, 99.9)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(result, Issue741RpcEvidenceJsonContext.Default.Issue741RpcEvidenceResult))
            .ConfigureAwait(false);
        Console.WriteLine($"#741 {result.RuntimeMode} {transport} {scenario} c{concurrency}: " +
            $"{completed} calls, {attempts} attempts, " +
            $"{result.ThroughputOperationsPerSecond:F1} ops/s, " +
            $"{result.AllocatedBytesPerOperation:F1} B/op, " +
            $"p99 {result.P99Us:F1} us");

        async Task WorkerAsync()
        {
            await startGate.Task.ConfigureAwait(false);
            while (Stopwatch.GetTimestamp() < measurementDeadline &&
                   Volatile.Read(ref completed) < latencies.Length - concurrency)
            {
                var id = Interlocked.Increment(ref nextCallId);
                var started = Stopwatch.GetTimestamp();
                await InvokeCheckedAsync(benchmark.Rpc, scenario, id).ConfigureAwait(false);
                var elapsed = Stopwatch.GetTimestamp() - started;
                var index = Interlocked.Increment(ref completed) - 1;
                latencies[index] = elapsed;
            }
        }
    }

    private static SharpClientBuilder CreateClient(SharpClientBuilder builder, string scenario)
    {
        if (scenario == "plain")
            return builder;

        return builder.UseRetry(options =>
        {
            options.MaxAttempts = 3;
            var backoff = scenario == "retry-backoff"
                ? TimeSpan.FromMilliseconds(2)
                : TimeSpan.Zero;
            options.InitialBackoff = backoff;
            options.MaxBackoff = backoff;
            options.JitterRatio = 0;
        });
    }

    private static async ValueTask InvokeCheckedAsync(
        IBenchmarkRpc rpc, string scenario, int id)
    {
        if (scenario == "retry-exhausted")
        {
            try
            {
                _ = await rpc.AddAsync(id, 20).ConfigureAwait(false);
            }
            catch (SharpLinkException exception)
                when (exception.Code == SharpLinkErrorCode.Unavailable)
            {
                return;
            }

            throw new InvalidOperationException("Retry-exhausted workload unexpectedly succeeded.");
        }

        var result = await rpc.AddAsync(id, 20).ConfigureAwait(false);
        if (result != id + 20)
            throw new InvalidOperationException($"Unary RPC returned {result}, expected {id + 20}.");
    }

    private static double Percentile(long[] sorted, int count, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * count / 100.0) - 1, 0, count - 1);
        return sorted[index] * 1_000_000.0 / Stopwatch.Frequency;
    }

    // Re-implement the contract interface over the existing generated benchmark service:
    // the same codec/dispatch route is exercised on base and candidate.
    private sealed class Issue741RetryProbeService(string scenario) : BenchmarkRpcService, IBenchmarkRpc
    {
        private readonly ConcurrentDictionary<int, byte> _needsRetry = new();
        private long _attempts;

        internal long Attempts => Interlocked.Read(ref _attempts);

        ValueTask<int> IBenchmarkRpc.AddAsync(int left, int right)
        {
            Interlocked.Increment(ref _attempts);
            if (scenario is "retry-once" or "retry-backoff")
            {
                if (_needsRetry.TryAdd(left, 0))
                    return ValueTask.FromException<int>(
                        new SharpLinkException(SharpLinkErrorCode.Unavailable, "Issue 741 forced retry."));
                _needsRetry.TryRemove(left, out _);
            }
            else if (scenario == "retry-exhausted")
            {
                return ValueTask.FromException<int>(
                    new SharpLinkException(SharpLinkErrorCode.Unavailable, "Issue 741 retry exhausted."));
            }

            return ValueTask.FromResult(left + right);
        }
    }
}

internal sealed class Issue741RpcEvidenceResult
{
    public string Commit { get; init; } = "";
    public string RuntimeMode { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public string RuntimeArchitecture { get; init; } = "";
    public int CpuCount { get; init; }
    public bool ServerGc { get; init; }
    public string Scenario { get; init; } = "";
    public string Transport { get; init; } = "";
    public int Concurrency { get; init; }
    public int WarmupMilliseconds { get; init; }
    public int MeasurementMilliseconds { get; init; }
    public double ActualMeasurementSeconds { get; init; }
    public int Operations { get; init; }
    public long PhysicalAttempts { get; init; }
    public int ExpectedAttemptsPerCall { get; init; }
    public int ValidationFailures { get; init; }
    public double ThroughputOperationsPerSecond { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
}

[JsonSerializable(typeof(Issue741RpcEvidenceResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal partial class Issue741RpcEvidenceJsonContext : JsonSerializerContext
{
}
