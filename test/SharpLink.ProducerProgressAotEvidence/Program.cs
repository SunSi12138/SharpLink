using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

/// <summary>
/// Fixed-work, identical-source JIT/NativeAOT control for issue #737.
/// No BDN, per-item probes, dynamic JSON metadata, or timer-bound operation count.
/// Every process executes exactly the same number of fully validated RPC streams.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException(
                "Usage: <tcp|sharedmemory> <Client100x16|ClientMulti2x100x4|ClientMulti2x1024x4Gated> " +
                "<warmup-ops> <measured-ops> <revision-sha> <output-file>");

        var transport = args[0];
        var scenario = args[1];
        var warmup = int.Parse(args[2], CultureInfo.InvariantCulture);
        var measured = int.Parse(args[3], CultureInfo.InvariantCulture);
        var revisionSha = args[4];
        var output = Path.GetFullPath(args[5]);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measured);
        if (revisionSha.Length != 40)
            throw new ArgumentException("Pass the 40-character Git SHA of the measured build.");

        // Both variants run with the same worker budget; do not let a rising ThreadPool minimum
        // become an unrecorded cross-revision difference.
        ThreadPool.GetMinThreads(out _, out var ioMin);
        ThreadPool.SetMinThreads(Math.Max(32, Environment.ProcessorCount * 4), ioMin);
        await using var environment = transport switch
        {
            "tcp" => await BenchmarkEnvironment.CreateAsync().ConfigureAwait(false),
            "sharedmemory" => await BenchmarkEnvironment.CreateSharedMemoryAsync().ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown transport: " + transport)
        };

        var (invoke, count) = CreateOperation(environment.Rpc, scenario);
        for (var i = 0; i < warmup; i++)
            await VerifyAsync(invoke, count).ConfigureAwait(false);

        // Compare allocations per fixed number of operations, not allocation/time divided by
        // a variable throughput-dependent denominator.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        var timings = new long[measured];
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocStart = GC.GetTotalAllocatedBytes(precise: true);
        var g0Start = GC.CollectionCount(0);
        var wallStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < measured; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await VerifyAsync(invoke, count).ConfigureAwait(false);
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        var elapsed = Stopwatch.GetElapsedTime(wallStart);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocStart;
        var g0 = GC.CollectionCount(0) - g0Start;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        Array.Sort(timings);

        var ops = measured;
        var builder = new StringBuilder();
        static void Add(StringBuilder b, string name, string value) => b.Append(name).Append('=').AppendLine(value);
        string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        Add(builder, "revision", revisionSha);
        Add(builder, "transport", transport);
        Add(builder, "scenario", scenario);
        Add(builder, "runtime", RuntimeInformation.FrameworkDescription);
        Add(builder, "architecture", RuntimeInformation.ProcessArchitecture.ToString());
        Add(builder, "processorCount", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        Add(builder, "measuredOperations", measured.ToString(CultureInfo.InvariantCulture));
        Add(builder, "itemsPerOperation", count.ToString(CultureInfo.InvariantCulture));
        Add(builder, "elapsedSeconds", N(elapsed.TotalSeconds));
        Add(builder, "throughputOperationsPerSecond", N(ops / elapsed.TotalSeconds));
        Add(builder, "cpuMicrosecondsPerOperation", N(cpu.TotalMicroseconds / ops));
        Add(builder, "allocationBytesTotal", allocated.ToString(CultureInfo.InvariantCulture));
        Add(builder, "allocationBytesPerOperation", N((double)allocated / ops));
        Add(builder, "allocationBytesPerItem", N((double)allocated / (ops * count)));
        Add(builder, "p50Microseconds", N(TicksToUs(timings[(int)Math.Floor((ops - 1) * 0.50)])));
        Add(builder, "p99Microseconds", N(TicksToUs(timings[(int)Math.Floor((ops - 1) * 0.99)])));
        Add(builder, "gen0Collections", g0.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, builder.ToString());
        Console.Write(builder.ToString());
        return 0;
    }

    private static double TicksToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static async Task VerifyAsync(Func<ValueTask<long>> operation, long expected)
    {
        var result = await operation().ConfigureAwait(false);
        if (result != expected)
            throw new InvalidOperationException($"Stream integrity failed: received {result}, expected {expected}");
    }

    private static (Func<ValueTask<long>> Run, long Expected) CreateOperation(
        IBenchmarkRpc rpc, string scenario)
    {
        if (scenario == "Client100x16")
        {
            var payload = BenchmarkRpcService.GetPayload(16);
            var values = Enumerable.Repeat(payload, 100).ToArray();
            var expected = 100L * BenchmarkRpcService.GetPayloadScore(payload);
            return (async () => await rpc.UploadPayloadsAsync(
                BenchmarkEnvironment.ToStream(values)).ConfigureAwait(false), expected);
        }

        if (scenario is "ClientMulti2x100x4" or "ClientMulti2x1024x4Gated")
        {
            var size = scenario == "ClientMulti2x100x4" ? 100 : 1_024;
            var left = Enumerable.Range(0, size).ToArray();
            var right = Enumerable.Range(size, size).ToArray();
            var expected = left.Sum(v => (long)v) + right.Sum(v => (long)v);
            if (scenario == "ClientMulti2x100x4")
            {
                return (async () => await rpc.MergeStreamsAsync(
                    BenchmarkEnvironment.ToStream(left),
                    BenchmarkEnvironment.ToStream(right)).ConfigureAwait(false), expected);
            }

            return (async () =>
            {
                var gate = new TwoProducerStartGate();
                return await rpc.MergeStreamsAsync(
                    ToGatedStream(left, gate),
                    ToGatedStream(right, gate)).ConfigureAwait(false);
            }, expected);
        }
        throw new ArgumentException("Unknown scenario: " + scenario);
    }

    private sealed class TwoProducerStartGate
    {
        private readonly TaskCompletionSource _bothEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2)
                _bothEntered.TrySetResult();
            return _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static async IAsyncEnumerable<int> ToGatedStream(
        IReadOnlyList<int> values,
        TwoProducerStartGate gate,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            yield return value;
            await Task.CompletedTask;
        }
    }
}
