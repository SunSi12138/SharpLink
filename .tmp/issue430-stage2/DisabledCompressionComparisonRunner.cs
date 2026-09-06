using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

public static class DisabledCompressionComparisonRunner
{
    private static readonly int[] Sizes = [4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024];
    private static readonly int[] Concurrencies = [1, 8, 32, 128];

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: --disabled-compression-evidence <output-json> <label>");
        var rows = new List<Row>();
        foreach (var transport in new[] { "tcp", "sharedmemory" })
        {
            await using var environment = transport == "tcp"
                ? await BenchmarkEnvironment.CreateAsync().ConfigureAwait(false)
                : await BenchmarkEnvironment.CreateSharedMemoryAsync().ConfigureAwait(false);
            foreach (var size in Sizes)
            {
                var payload = new string('x', size);
                foreach (var concurrency in Concurrencies)
                    rows.Add(await MeasureAsync(environment.Rpc, transport, payload, concurrency).ConfigureAwait(false));
            }
        }
        var document = new { label = args[1], framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount, rows };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        await File.WriteAllTextAsync(args[0], json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<Row> MeasureAsync(IBenchmarkRpc rpc, string transport, string payload, int concurrency)
    {
        for (var i = 0; i < 4; i++) { var warm = await rpc.EchoAsync(payload).ConfigureAwait(false); if (warm.Length != payload.Length) throw new InvalidOperationException("warmup mismatch"); }
        var operations = Math.Max(concurrency, Math.Clamp((16 * 1024 * 1024) / payload.Length, 32, 1024));
        var latencies = new long[operations];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var next = -1;
        var workers = new Task[Math.Min(concurrency, operations)];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= operations) return;
                    var start = Stopwatch.GetTimestamp();
                    var response = await rpc.EchoAsync(payload).ConfigureAwait(false);
                    latencies[index] = Stopwatch.GetTimestamp() - start;
                    if (response.Length != payload.Length) throw new InvalidOperationException("payload mismatch");
                }
            });
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        watch.Stop(); process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(true);
        Array.Sort(latencies);
        return new Row { Transport = transport, PayloadChars = payload.Length, Concurrency = concurrency, Samples = operations, Qps = operations / watch.Elapsed.TotalSeconds, P50Ms = Percentile(latencies, .50), P99Ms = Percentile(latencies, .99), P999Ms = Percentile(latencies, .999), CpuUsPerOp = (cpuAfter - cpuBefore).TotalMilliseconds * 1000d / operations, AllocatedBytesPerOp = (allocatedAfter - allocatedBefore) / (double)operations, Gen0 = GC.CollectionCount(0) - g0, Gen1 = GC.CollectionCount(1) - g1, Gen2 = GC.CollectionCount(2) - g2 };
    }

    private static double Percentile(long[] values, double p)
    {
        var index = Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1);
        return values[index] * 1000d / Stopwatch.Frequency;
    }

    public sealed class Row
    {
        public string Transport { get; init; } = "";
        public int PayloadChars { get; init; }
        public int Concurrency { get; init; }
        public int Samples { get; init; }
        public double Qps { get; init; }
        public double P50Ms { get; init; }
        public double P99Ms { get; init; }
        public double P999Ms { get; init; }
        public double CpuUsPerOp { get; init; }
        public double AllocatedBytesPerOp { get; init; }
        public int Gen0 { get; init; }
        public int Gen1 { get; init; }
        public int Gen2 { get; init; }
    }
}
