using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class AdmissionPartitionC1Diagnostic
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: --issue-245-c1-diagnostic <label> <output-json>");

        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 256), io);

        var label = args[0];
        var results = new List<C1DiagnosticResult>();
        results.Add(await RunRpcAsync(label, "rpc-disabled", 1).ConfigureAwait(false));
        results.Add(await RunRpcAsync(label, "rpc-global", 1).ConfigureAwait(false));
        foreach (var concurrency in new[] { 1, 2, 4, 8, 16, 32 })
            results.Add(await RunRpcAsync(label, "rpc-partitioned", concurrency).ConfigureAwait(false));

        var path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        })).ConfigureAwait(false);

        foreach (var result in results)
        {
            Console.WriteLine(
                $"ISSUE245_C1 label={result.Label} kind={result.Kind} c={result.Concurrency} " +
                $"ops={result.Operations} qps={result.Qps:F0} p99_us={result.P99Us:F3} " +
                $"cpu_us_op={result.CpuUsPerOperation:F3} alloc_b_op={result.AllocatedBytesPerOperation:F1}");
        }
    }

    private static async Task<C1DiagnosticResult> RunRpcAsync(string label, string kind, int concurrency)
    {
        const int partitions = 1024;
        var keys = Enumerable.Range(0, partitions).Select(static i => $"partition-{i}").ToArray();
        var selectorIndex = -1;

        BenchmarkEnvironment environment;
        if (kind == "rpc-disabled")
        {
            environment = await BenchmarkEnvironment.CreateAsync().ConfigureAwait(false);
        }
        else if (kind == "rpc-global")
        {
            environment = await BenchmarkEnvironment.CreateAsync(
                configureServer: builder => builder.UseAdmissionControl(
                    options => options.Global.UseConcurrency(4096))).ConfigureAwait(false);
        }
        else
        {
            environment = await BenchmarkEnvironment.CreateAsync(
                configureServer: builder => builder.UseAdmissionControl(options =>
                    options.UsePartition(
                        _ => keys[(Interlocked.Increment(ref selectorIndex) & int.MaxValue) % keys.Length],
                        partition =>
                        {
                            partition.MaxPartitions = partitions;
                            partition.IdleTimeout = TimeSpan.FromMinutes(5);
                            partition.UseConcurrency(4096);
                        }))).ConfigureAwait(false);
        }

        await using (environment.ConfigureAwait(false))
        {
            for (var i = 0; i < 20_000; i++)
            {
                var value = await environment.Rpc.AddAsync(10, 20).ConfigureAwait(false);
                if (value != 30)
                    throw new InvalidOperationException("warmup failed");
            }
            Interlocked.Exchange(ref selectorIndex, -1);
            await Task.Delay(100).ConfigureAwait(false);

            var perWorker = Math.Max(2_000, 50_000 / concurrency);
            var operations = checked(perWorker * concurrency);
            var latencies = new long[operations];
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new Task[concurrency];
            for (var worker = 0; worker < concurrency; worker++)
            {
                var workerIndex = worker;
                tasks[worker] = Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    var offset = workerIndex * perWorker;
                    for (var i = 0; i < perWorker; i++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var value = await environment.Rpc.AddAsync(10, 20).ConfigureAwait(false);
                        latencies[offset + i] = Stopwatch.GetTimestamp() - started;
                        if (value != 30)
                            throw new InvalidOperationException("measurement failed");
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
            var rank = Math.Clamp((int)Math.Ceiling(0.99d * latencies.Length) - 1, 0, latencies.Length - 1);
            return new C1DiagnosticResult
            {
                Label = label,
                Kind = kind,
                Concurrency = concurrency,
                Operations = operations,
                Qps = operations / elapsed.TotalSeconds,
                P99Us = latencies[rank] * 1_000_000d / Stopwatch.Frequency,
                CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / operations,
                AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)operations
            };
        }
    }
}

internal sealed class C1DiagnosticResult
{
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public int Operations { get; init; }
    public double Qps { get; init; }
    public double P99Us { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
}
