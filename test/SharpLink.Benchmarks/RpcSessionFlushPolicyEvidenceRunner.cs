using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.Benchmarks;

/// <summary>Issue #590 same-machine steady-state evidence for runtime RPC flush policy support.</summary>
public static class RpcSessionFlushPolicyEvidenceRunner
{
    private const int WarmupOperations = 256;
    private const int OperationsPerRound = 1024;
    private const int Rounds = 5;
    private static readonly int[] PayloadSizes = [32, 4096];
    private static readonly int[] ConcurrencyLevels = [1, 8, 32];
    private static readonly SharpLinkPerformanceProfile[] Profiles =
    [
        SharpLinkPerformanceProfile.Balanced,
        SharpLinkPerformanceProfile.Throughput
    ];

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --rpc-session-flush-policy-evidence <static-baseline|runtime-no-update|post-update> <output-json>");
        }

        var state = args[0];
        if (state is not ("static-baseline" or "runtime-no-update" or "post-update"))
            throw new ArgumentOutOfRangeException(nameof(args), state, "Unknown RPC flush evidence state.");

        var outputPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var measurements = new List<RpcSessionFlushPolicyMeasurement>();

        foreach (var profile in Profiles)
        {
            var (threshold, latency) = GetPolicy(profile);
            foreach (var payloadBytes in PayloadSizes)
            {
                foreach (var concurrency in ConcurrencyLevels)
                {
                    var payload = new byte[payloadBytes];
                    Array.Fill(payload, (byte)0x2a);
                    await using var environment = await BenchmarkEnvironment.CreateAsync(
                        configureServer: server => server.UseRpcSessionFlush(threshold, latency),
                        configureServerRuntime: options => options.PerformanceProfile = profile,
                        configureClientRuntime: options => options.PerformanceProfile = profile,
                        createClientBuilder: port => SharpClientBuilder.Create()
                            .UseTcp(IPAddress.Loopback.ToString(), port)
                            .UseRpcSessionFlush(threshold, latency))
                        .ConfigureAwait(false);

                    if (state == "post-update")
                    {
                        ApplyRepeatedUpdates(environment.Client, environment.Server, threshold, latency);
                    }

                    measurements.Add(await MeasureAsync(
                        state,
                        profile,
                        threshold,
                        latency,
                        payload,
                        concurrency,
                        environment.Rpc).ConfigureAwait(false));
                }
            }
        }

        var document = new RpcSessionFlushPolicyEvidenceDocument
        {
            State = state,
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            WarmupOperations = WarmupOperations,
            OperationsPerRound = OperationsPerRound,
            Rounds = Rounds,
            Measurements = measurements,
            Notes =
            [
                "All rows use the same generated unary byte[] EchoBytes RPC over loopback TCP and the same benchmark harness source copied into both the base and PR worktrees.",
                "Both base and PR-head measurements use explicit timed flush policies so the effective batching semantics are identical while the implementation changes from static pump fields to runtime-capable immutable generations.",
                "post-update performs eight alternate->original policy replacement cycles on the same live Client and Server before measurement, ending on the exact original threshold/latency without reconnecting.",
                "Allocated bytes are process-wide GC allocations divided by logical RPCs and therefore include the full loopback Client/Server path and harness; compare states for the same profile/payload/concurrency rather than treating them as the isolated cost of Capture().",
                "P50/P99 are per-RPC wall-clock samples recorded with Stopwatch.GetTimestamp. QPS, CPU/op, allocation, P50 and P99 are the median of five rounds after warmup and full GC."
            ]
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<RpcSessionFlushPolicyMeasurement> MeasureAsync(
        string state,
        SharpLinkPerformanceProfile profile,
        int threshold,
        TimeSpan latency,
        byte[] payload,
        int concurrency,
        IBenchmarkRpc rpc)
    {
        await RunOperationsAsync(rpc, payload, concurrency, WarmupOperations, null).ConfigureAwait(false);

        var samples = new List<RpcSessionFlushPolicyRound>(Rounds);
        for (var round = 0; round < Rounds; round++)
        {
            var latencyTicks = new long[OperationsPerRound];
            var workerTasks = new Task[concurrency];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();

            await RunOperationsAsync(
                rpc,
                payload,
                concurrency,
                OperationsPerRound,
                latencyTicks,
                workerTasks).ConfigureAwait(false);

            watch.Stop();
            process.Refresh();
            var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            var cpuAfter = process.TotalProcessorTime;
            Array.Sort(latencyTicks);
            samples.Add(new RpcSessionFlushPolicyRound(
                Qps: OperationsPerRound / Math.Max(watch.Elapsed.TotalSeconds, double.Epsilon),
                AllocatedBytesPerOperation: (allocatedAfter - allocatedBefore) / (double)OperationsPerRound,
                CpuMicrosecondsPerOperation: (cpuAfter - cpuBefore).TotalMilliseconds * 1000d / OperationsPerRound,
                P50Microseconds: ToMicroseconds(Percentile(latencyTicks, 0.50)),
                P99Microseconds: ToMicroseconds(Percentile(latencyTicks, 0.99))));
        }

        return new RpcSessionFlushPolicyMeasurement
        {
            State = state,
            Profile = profile.ToString(),
            FlushSizeThreshold = threshold,
            MaxLatencyMicroseconds = latency.TotalMilliseconds * 1000d,
            PayloadBytes = payload.Length,
            Concurrency = concurrency,
            Qps = Median(samples.Select(static sample => sample.Qps)),
            AllocatedBytesPerOperation = Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)),
            CpuMicrosecondsPerOperation = Median(samples.Select(static sample => sample.CpuMicrosecondsPerOperation)),
            P50Microseconds = Median(samples.Select(static sample => sample.P50Microseconds)),
            P99Microseconds = Median(samples.Select(static sample => sample.P99Microseconds))
        };
    }

    private static Task RunOperationsAsync(
        IBenchmarkRpc rpc,
        byte[] payload,
        int concurrency,
        int operationCount,
        long[]? latencyTicks,
        Task[]? workers = null)
    {
        workers ??= new Task[concurrency];
        var state = new RoundState(rpc, payload, operationCount, latencyTicks);
        for (var index = 0; index < concurrency; index++)
            workers[index] = RunWorkerAsync(state);
        return Task.WhenAll(workers);
    }

    private static async Task RunWorkerAsync(RoundState state)
    {
        while (true)
        {
            var operation = Interlocked.Increment(ref state.NextOperation) - 1;
            if (operation >= state.OperationCount)
                return;

            var started = Stopwatch.GetTimestamp();
            var response = await state.Rpc.EchoBytesAsync(state.Payload).ConfigureAwait(false);
            var finished = Stopwatch.GetTimestamp();
            if (response.Length != state.Payload.Length ||
                response[0] != 0x2a ||
                response[^1] != 0x2a)
            {
                throw new InvalidOperationException("RPC flush performance evidence received an unexpected EchoBytes payload.");
            }
            if (state.LatencyTicks is not null)
                state.LatencyTicks[operation] = finished - started;
        }
    }

    private static void ApplyRepeatedUpdates(object client, object server, int threshold, TimeSpan latency)
    {
        var alternateThreshold = Math.Max(1, threshold / 2);
        var alternateLatency = TimeSpan.FromTicks(checked(latency.Ticks * 2));
        var clientUpdate = FindUpdateMethod(
            "SharpLink.Client.SharpLinkClientRpcSessionFlushExtensions, SharpLink.Client");
        var serverUpdate = FindUpdateMethod(
            "SharpLink.Server.SharpLinkServerRpcSessionFlushExtensions, SharpLink.Server");

        for (var iteration = 0; iteration < 8; iteration++)
        {
            clientUpdate.Invoke(null, [client, alternateThreshold, alternateLatency]);
            serverUpdate.Invoke(null, [server, alternateThreshold, alternateLatency]);
            clientUpdate.Invoke(null, [client, threshold, latency]);
            serverUpdate.Invoke(null, [server, threshold, latency]);
        }
    }

    private static MethodInfo FindUpdateMethod(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: true)!;
        return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static method =>
                method.Name == "UpdateRpcSessionFlushPolicy" && method.GetParameters().Length == 3);
    }

    private static (int Threshold, TimeSpan Latency) GetPolicy(SharpLinkPerformanceProfile profile)
        => profile switch
        {
            SharpLinkPerformanceProfile.Throughput => (64 * 1024, TimeSpan.FromMilliseconds(2)),
            _ => (16 * 1024, TimeSpan.FromMilliseconds(1))
        };

    private static long Percentile(long[] values, double percentile)
    {
        var position = percentile * (values.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, values.Length - 1);
        if (lower == upper)
            return values[lower];
        var fraction = position - lower;
        return (long)Math.Round(values[lower] + ((values[upper] - values[lower]) * fraction));
    }

    private static double ToMicroseconds(long stopwatchTicks)
        => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private sealed class RoundState(
        IBenchmarkRpc rpc,
        byte[] payload,
        int operationCount,
        long[]? latencyTicks)
    {
        internal readonly IBenchmarkRpc Rpc = rpc;
        internal readonly byte[] Payload = payload;
        internal readonly int OperationCount = operationCount;
        internal readonly long[]? LatencyTicks = latencyTicks;
        internal int NextOperation;
    }

    private sealed class RpcSessionFlushPolicyEvidenceDocument
    {
        public string State { get; init; } = string.Empty;
        public string Commit { get; init; } = string.Empty;
        public string Framework { get; init; } = string.Empty;
        public string Os { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public int WarmupOperations { get; init; }
        public int OperationsPerRound { get; init; }
        public int Rounds { get; init; }
        public List<RpcSessionFlushPolicyMeasurement> Measurements { get; init; } = [];
        public List<string> Notes { get; init; } = [];
    }

    private sealed class RpcSessionFlushPolicyMeasurement
    {
        public string State { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public int FlushSizeThreshold { get; init; }
        public double MaxLatencyMicroseconds { get; init; }
        public int PayloadBytes { get; init; }
        public int Concurrency { get; init; }
        public double Qps { get; init; }
        public double AllocatedBytesPerOperation { get; init; }
        public double CpuMicrosecondsPerOperation { get; init; }
        public double P50Microseconds { get; init; }
        public double P99Microseconds { get; init; }
    }

    private readonly record struct RpcSessionFlushPolicyRound(
        double Qps,
        double AllocatedBytesPerOperation,
        double CpuMicrosecondsPerOperation,
        double P50Microseconds,
        double P99Microseconds);
}
