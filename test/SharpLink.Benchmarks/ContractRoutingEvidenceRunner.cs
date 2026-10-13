using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>Matched generated-RPC measurements with explicit OneWay completion boundaries.</summary>
internal static class ContractRoutingEvidenceRunner
{
    private const int StreamItems = 32;
    private static readonly int[] Numbers = Enumerable.Range(0, StreamItems).ToArray();
    private static readonly string[] Messages = Numbers.Select(static value => $"message-{value}").ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 5)
            throw new ArgumentException("Usage: --contract-routing-evidence <tcp|sharedmemory> <unary|oneway-sender|oneway-receiver|clientstream|serverstream|duplex> <warmup-operations> <measured-operations> <output-json>");

        var transport = args[0];
        var scenario = args[1];
        var warmup = int.Parse(args[2], CultureInfo.InvariantCulture);
        var operations = int.Parse(args[3], CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfLessThan(operations, 1_000);
        if (scenario is not ("unary" or "oneway-sender" or "oneway-receiver" or "clientstream" or "serverstream" or "duplex"))
            throw new ArgumentException($"Unknown scenario {scenario}.");
        var output = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = new ContractRoutingMeasurement
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Transport = transport,
            Scenario = scenario,
            WarmupOperations = warmup,
            RequestedOperations = operations,
            StreamItemsPerOperation = scenario is "clientstream" or "serverstream" or "duplex" ? StreamItems : 0,
            CompletionBoundary = scenario == "oneway-sender" ? "sender-queued" : "receiver-completed",
            LatencyTicks = new long[operations]
        };
        try
        {
            RequireReleaseBuild();
            var receiver = new BenchmarkRpcService();
            await using var environment = transport switch
            {
                "tcp" => await BenchmarkEnvironment.CreateAsync(
                    configureServer: server =>
                    {
                        server.ReplaceService<IBenchmarkRpc>(receiver);
                        server.UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
                    },
                    configureServerRuntime: static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced,
                    configureClientRuntime: static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced,
                    createClientBuilder: port => SharpClientBuilder.Create()
                        .UseTcp("127.0.0.1", port)
                        .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
                        .UseConnectionPool(static options =>
                        {
                            options.MinConnections = 1;
                            options.MaxConnections = 1;
                        })).ConfigureAwait(false),
                "sharedmemory" => await BenchmarkEnvironment.CreateSharedMemoryAsync(
                    configureServerRuntime: static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced,
                    configureClientRuntime: static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown transport {transport}.")
            };
            if (transport == "sharedmemory")
                receiver = environment.LocalService;

            var isOneWay = scenario.StartsWith("oneway-", StringComparison.Ordinal);
            var publishedTarget = receiver.PublishedCount;
            for (var index = 0; index < warmup; index++)
            {
                await InvokeAsync(environment.Rpc, receiver, scenario, ++publishedTarget).ConfigureAwait(false);
            }
            if (isOneWay)
                WaitForPublished(receiver, publishedTarget);
            if (await environment.Rpc.AddAsync(20, 22).ConfigureAwait(false) != 42)
                throw new InvalidOperationException("Pre-measurement generated RPC control failed.");

            using var process = Process.GetCurrentProcess();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var publishedBefore = receiver.PublishedCount;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < operations; index++)
            {
                var operationStarted = Stopwatch.GetTimestamp();
                await InvokeAsync(environment.Rpc, receiver, scenario, ++publishedTarget).ConfigureAwait(false);
                result.LatencyTicks[index] = Stopwatch.GetTimestamp() - operationStarted;
                result.CompletedOperations++;
            }
            var completion = Stopwatch.GetTimestamp();
            result.ElapsedSeconds = (completion - started) / (double)Stopwatch.Frequency;
            result.ThroughputPerSecond = operations / result.ElapsedSeconds;
            result.ReceiverObservedAtSendCompletion = isOneWay ? receiver.PublishedCount - publishedBefore : operations;

            // The sender timer has stopped. This count barrier supplies separate evidence of
            // business-handler entry; it is not a wire ACK or a per-request delivery promise.
            if (isOneWay)
                WaitForPublished(receiver, publishedTarget);
            var receiverCompleted = Stopwatch.GetTimestamp();
            var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            result.ReceiverCompletedOperations = isOneWay ? receiver.PublishedCount - publishedBefore : operations;
            if (result.ReceiverCompletedOperations != operations)
                throw new InvalidOperationException("Receiver completion count differs from the exact sent count.");
            result.ReceiverCompletedElapsedSeconds = (receiverCompleted - started) / (double)Stopwatch.Frequency;
            result.ReceiverCompletedThroughputPerSecond = operations / result.ReceiverCompletedElapsedSeconds;
            result.DrainSecondsAfterSendCompletion = (receiverCompleted - completion) / (double)Stopwatch.Frequency;
            result.ProcessWideAllocatedBytesPerCompletedOperation = (allocatedAfter - allocatedBefore) / (double)operations;
            result.ProcessCpuMicrosecondsPerCompletedOperation = (process.TotalProcessorTime - cpuBefore).TotalMicroseconds / operations;
            var ordered = (long[])result.LatencyTicks.Clone();
            Array.Sort(ordered);
            result.P50Microseconds = Percentile(ordered, 0.50);
            result.P99Microseconds = Percentile(ordered, 0.99);
            result.P999Microseconds = Percentile(ordered, 0.999);
            result.MaximumMicroseconds = ordered[^1] * 1_000_000d / Stopwatch.Frequency;
            result.Passed = true;
        }
        catch (Exception error)
        {
            result.Passed = false;
            result.Failure = error.ToString();
            Environment.ExitCode = 1;
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        Console.WriteLine($"{transport}/{scenario}: passed={result.Passed}, completed={result.CompletedOperations}/{operations}, {result.ThroughputPerSecond:F0} operations/s, p99={result.P99Microseconds:F3} us; boundary={result.CompletionBoundary}");
        if (!result.Passed)
            Console.Error.WriteLine(result.Failure);
    }

    private static async ValueTask InvokeAsync(IBenchmarkRpc rpc, BenchmarkRpcService receiver, string scenario, long publishedTarget)
    {
        switch (scenario)
        {
            case "unary":
                if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42)
                    throw new InvalidOperationException("Unary returned an incorrect value.");
                break;
            case "oneway-sender":
            case "oneway-receiver":
                await rpc.PublishEventAsync(7, 11, "bench").ConfigureAwait(false);
                if (scenario == "oneway-receiver")
                    WaitForPublished(receiver, publishedTarget);
                break;
            case "clientstream":
                if (await rpc.UploadNumbersAsync(BenchmarkEnvironment.ToStream(Numbers)).ConfigureAwait(false) != StreamItems * (StreamItems - 1) / 2)
                    throw new InvalidOperationException("Client stream returned an incorrect sum.");
                break;
            case "serverstream":
                var count = 0;
                await foreach (var value in rpc.DownloadNumbersAsync(StreamItems).ConfigureAwait(false))
                {
                    if (value != count++)
                        throw new InvalidOperationException("Server stream returned an incorrect item.");
                }
                if (count != StreamItems)
                    throw new InvalidOperationException("Server stream ended at the wrong item count.");
                break;
            case "duplex":
                var position = 0;
                await foreach (var value in rpc.DuplexAsync(BenchmarkEnvironment.ToStream(Messages)).ConfigureAwait(false))
                {
                    if (position >= Messages.Length || value != Messages[position++])
                        throw new InvalidOperationException("Duplex stream returned an incorrect item.");
                }
                if (position != StreamItems)
                    throw new InvalidOperationException("Duplex stream ended at the wrong item count.");
                break;
            default:
                throw new ArgumentException($"Unknown scenario {scenario}.");
        }
    }

    private static void WaitForPublished(BenchmarkRpcService receiver, long expected)
    {
        var deadline = Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency;
        var spin = new SpinWait();
        while (receiver.PublishedCount < expected)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException($"OneWay receiver did not execute operation {expected} within ten seconds.");
            spin.SpinOnce();
        }
    }

    private static void RequireReleaseBuild()
    {
#if DEBUG
        throw new InvalidOperationException("Contract routing performance evidence requires a Release build.");
#endif
    }

    private static double Percentile(long[] ordered, double percentile)
        => ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)] * 1_000_000d / Stopwatch.Frequency;
}

internal sealed class ContractRoutingMeasurement
{
    public int SchemaVersion { get; init; } = 1;
    public string Commit { get; init; } = string.Empty;
    public string HostName { get; init; } = Environment.MachineName;
    public string Runtime { get; init; } = RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public bool ServerGc { get; init; } = GCSettings.IsServerGC;
    public string Transport { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string CompletionBoundary { get; init; } = string.Empty;
    public string PerformanceProfile { get; init; } = "Balanced";
    public int Connections { get; init; } = 1;
    public int Concurrency { get; init; } = 1;
    public int WarmupOperations { get; init; }
    public int RequestedOperations { get; init; }
    public int StreamItemsPerOperation { get; init; }
    public long StopwatchFrequency { get; init; } = Stopwatch.Frequency;
    public int CompletedOperations { get; set; }
    public long ReceiverObservedAtSendCompletion { get; set; }
    public long ReceiverCompletedOperations { get; set; }
    public double ElapsedSeconds { get; set; }
    public double ThroughputPerSecond { get; set; }
    public double ReceiverCompletedElapsedSeconds { get; set; }
    public double ReceiverCompletedThroughputPerSecond { get; set; }
    public double DrainSecondsAfterSendCompletion { get; set; }
    public double ProcessWideAllocatedBytesPerCompletedOperation { get; set; }
    public double ProcessCpuMicrosecondsPerCompletedOperation { get; set; }
    public double P50Microseconds { get; set; }
    public double P99Microseconds { get; set; }
    public double P999Microseconds { get; set; }
    public double MaximumMicroseconds { get; set; }
    public long[] LatencyTicks { get; init; } = [];
    public bool Passed { get; set; }
    public string? Failure { get; set; }
}
