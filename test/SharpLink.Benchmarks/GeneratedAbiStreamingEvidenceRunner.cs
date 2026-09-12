using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class GeneratedAbiStreamingEvidenceRunner
{
    private static readonly JsonSerializerOptions SJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions SJsonLinesOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 5)
        {
            throw new ArgumentException(
                "Usage: --generated-abi-streaming-evidence <scenario> " +
                "<warmup-operations> <measurement-seconds> <max-operations> <output-json>");
        }

        var scenario = Enum.Parse<GeneratedAbiStreamingScenario>(args[0], ignoreCase: true);
        var warmupOperations = int.Parse(args[1], CultureInfo.InvariantCulture);
        var measurementSeconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[3], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[4]);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupOperations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measurementSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);

        await using var benchmark = await GeneratedAbiStreamingCase.CreateAsync(scenario)
            .ConfigureAwait(false);
        var firstStarted = Stopwatch.GetTimestamp();
        var firstResult = await benchmark.InvokeAsync().ConfigureAwait(false);
        var firstCallUs = Stopwatch.GetElapsedTime(firstStarted).TotalMicroseconds;
        Validate(firstResult, benchmark.ExpectedResult, scenario, "first call");

        for (var operation = 0; operation < warmupOperations; operation++)
        {
            var result = await benchmark.InvokeAsync().ConfigureAwait(false);
            Validate(result, benchmark.ExpectedResult, scenario, "warmup");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var latencies = new long[maxOperations];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var measurementStarted = Stopwatch.GetTimestamp();
        var measurementDeadline = measurementStarted +
            checked((long)Math.Ceiling(measurementSeconds * Stopwatch.Frequency));
        var completed = 0;
        long latencyTicks = 0;

        while (completed < latencies.Length && Stopwatch.GetTimestamp() < measurementDeadline)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await benchmark.InvokeAsync().ConfigureAwait(false);
            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            Validate(result, benchmark.ExpectedResult, scenario, "measurement");
            latencies[completed++] = elapsedTicks;
            latencyTicks += elapsedTicks;
        }

        var measurementElapsed = Stopwatch.GetElapsedTime(measurementStarted);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (completed == 0)
            throw new InvalidOperationException("The generated ABI evidence run completed no operations.");

        Array.Sort(latencies, 0, completed);
        var allocatedBytes = allocatedAfter - allocatedBefore;
        var resultDocument = new GeneratedAbiStreamingEvidenceResult
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Scenario = scenario.ToString(),
            Shape = benchmark.Shape,
            ItemCount = benchmark.ItemCount,
            ItemBytes = benchmark.ItemBytes,
            TimestampUtc = DateTimeOffset.UtcNow,
            HostName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
            WarmupOperations = warmupOperations,
            RequestedMeasurementSeconds = measurementSeconds,
            ActualMeasurementSeconds = measurementElapsed.TotalSeconds,
            Operations = completed,
            ThroughputOperationsPerSecond = completed / measurementElapsed.TotalSeconds,
            ThroughputItemsPerSecond = completed * benchmark.ItemCount / measurementElapsed.TotalSeconds,
            FirstCallUs = firstCallUs,
            AverageUs = TicksToMicroseconds(latencyTicks / (double)completed),
            P50Us = Percentile(latencies, completed, 50),
            P99Us = Percentile(latencies, completed, 99),
            P999Us = Percentile(latencies, completed, 99.9),
            MaxUs = TicksToMicroseconds(latencies[completed - 1]),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = allocatedBytes / (double)completed,
            AllocatedBytesPerItem = allocatedBytes / (double)(completed * benchmark.ItemCount),
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            ThreadCount = process.Threads.Count,
            WorkingSetBytes = process.WorkingSet64,
            ValidationFailures = 0,
            HitOperationLimit = completed == maxOperations
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(resultDocument, SJsonOptions)).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(resultDocument, SJsonOptions));
    }

    public static async Task SummarizeAsync(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException(
                "Usage: --summarize-generated-abi-streaming-evidence " +
                "<input-directory> <output-markdown> <output-jsonl>");
        }

        var inputDirectory = Path.GetFullPath(args[0]);
        var markdownPath = Path.GetFullPath(args[1]);
        var jsonLinesPath = Path.GetFullPath(args[2]);
        var results = new List<GeneratedAbiStreamingEvidenceResult>();
        foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.json", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<GeneratedAbiStreamingEvidenceResult>(
                stream,
                SJsonOptions).ConfigureAwait(false);
            if (result is not null)
                results.Add(result);
        }
        if (results.Count == 0)
            throw new InvalidOperationException("No generated ABI evidence JSON files were found.");

        var markdown = new StringBuilder();
        markdown.AppendLine("# P3-00 generated API3 streaming baseline");
        markdown.AppendLine();
        markdown.AppendLine($"- Commit: `{SingleValue(results.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Host: `{SingleValue(results.Select(static item => item.HostName))}`");
        markdown.AppendLine($"- Runtime: `{SingleValue(results.Select(static item => item.RuntimeVersion))}`");
        markdown.AppendLine($"- OS/architecture: `{SingleValue(results.Select(static item => $"{item.OperatingSystem} / {item.Architecture}"))}`");
        markdown.AppendLine($"- Tiered compilation / PGO: `{SingleValue(results.Select(static item => $"{item.TieredCompilation}/{item.TieredPgo}"))}`");
        markdown.AppendLine($"- Raw runs: {results.Count}; validation failures: {results.Sum(static item => item.ValidationFailures)}");
        markdown.AppendLine();
        markdown.AppendLine("| Scenario | Runs | Items | Item bytes | Ops/s | Items/s | P50 us | P99 us | CPU us/op | Alloc B/op | Alloc B/item |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in results.GroupBy(static item => item.Scenario).OrderBy(static group => group.Key))
        {
            var items = group.ToArray();
            markdown.AppendLine(
                $"| {group.Key} | {items.Length} | {SingleValue(items.Select(static item => item.ItemCount))} | " +
                $"{SingleValue(items.Select(static item => item.ItemBytes))} | " +
                $"{Median(items.Select(static item => item.ThroughputOperationsPerSecond)):F1} | " +
                $"{Median(items.Select(static item => item.ThroughputItemsPerSecond)):F1} | " +
                $"{Median(items.Select(static item => item.P50Us)):F2} | " +
                $"{Median(items.Select(static item => item.P99Us)):F2} | " +
                $"{Median(items.Select(static item => item.CpuUsPerOperation)):F2} | " +
                $"{Median(items.Select(static item => item.AllocatedBytesPerOperation)):F0} | " +
                $"{Median(items.Select(static item => item.AllocatedBytesPerItem)):F1} |");
        }
        markdown.AppendLine();
        markdown.AppendLine("## Interpretation constraints");
        markdown.AppendLine();
        markdown.AppendLine("- Each operation completes one full RPC stream; per-item allocation is the process-wide client/server managed allocation delta divided by completed items.");
        markdown.AppendLine("- Payload instances are reused at the producer to avoid source-data setup noise; serialization, transport, deserialization, and stream lifecycle remain measured.");
        markdown.AppendLine("- Every operation validates item count, payload length, and first/last-byte sentinels through a deterministic score.");
        markdown.AppendLine("- P3-01 and P3-GATE must run the identical scenarios and runner on the same host and CPU affinity.");

        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonLinesPath)!);
        await File.WriteAllTextAsync(markdownPath, markdown.ToString()).ConfigureAwait(false);
        var jsonLines = string.Join(
            Environment.NewLine,
            results
                .OrderBy(static item => item.Scenario, StringComparer.Ordinal)
                .ThenBy(static item => item.TimestampUtc)
                .Select(static item => JsonSerializer.Serialize(item, SJsonLinesOptions)));
        await File.WriteAllTextAsync(jsonLinesPath, jsonLines + Environment.NewLine).ConfigureAwait(false);
    }

    private static string SingleValue(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? distinct[0] : string.Join(", ", distinct);
    }

    private static string SingleValue(IEnumerable<int> values)
    {
        var distinct = values.Distinct().ToArray();
        return distinct.Length == 1
            ? distinct[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", distinct);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return double.NaN;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static double Percentile(long[] values, int count, double percentile)
    {
        var rank = Math.Clamp((int)Math.Ceiling(percentile / 100 * count) - 1, 0, count - 1);
        return TicksToMicroseconds(values[rank]);
    }

    private static double TicksToMicroseconds(double ticks)
        => ticks * 1_000_000d / Stopwatch.Frequency;

    private static void Validate(
        long actual,
        long expected,
        GeneratedAbiStreamingScenario scenario,
        string phase)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{scenario} returned score {actual} instead of {expected} during {phase}.");
        }
    }
}

internal enum GeneratedAbiStreamingScenario
{
    Server1x16,
    Server100x16,
    Server100x4096,
    Client100x16,
    Client100x4096,
    Duplex100x16,
    Duplex100x4096
}

internal sealed class GeneratedAbiStreamingCase : IAsyncDisposable
{
    private readonly BenchmarkEnvironment _environment;

    private GeneratedAbiStreamingCase(
        BenchmarkEnvironment environment,
        GeneratedAbiStreamingScenario scenario,
        string shape,
        int itemCount,
        int itemBytes,
        Func<ValueTask<long>> invokeAsync)
    {
        _environment = environment;
        Scenario = scenario;
        Shape = shape;
        ItemCount = itemCount;
        ItemBytes = itemBytes;
        InvokeAsync = invokeAsync;
        ExpectedResult = itemCount * BenchmarkRpcService.GetPayloadScore(
            BenchmarkRpcService.GetPayload(itemBytes));
    }

    public GeneratedAbiStreamingScenario Scenario { get; }
    public string Shape { get; }
    public int ItemCount { get; }
    public int ItemBytes { get; }
    public long ExpectedResult { get; }
    public Func<ValueTask<long>> InvokeAsync { get; }

    public static async Task<GeneratedAbiStreamingCase> CreateAsync(
        GeneratedAbiStreamingScenario scenario)
    {
        var environment = await BenchmarkEnvironment.CreateAsync().ConfigureAwait(false);
        try
        {
            var (shape, itemCount, itemBytes) = GetDimensions(scenario);
            var payload = BenchmarkRpcService.GetPayload(itemBytes);
            var payloads = Enumerable.Repeat(payload, itemCount).ToArray();
            Func<ValueTask<long>> invoke = shape switch
            {
                "ServerStreaming" => () => InvokeServerStreamingAsync(
                    environment.Rpc,
                    itemCount,
                    itemBytes),
                "ClientStreaming" => async () => await environment.Rpc.UploadPayloadsAsync(
                    BenchmarkEnvironment.ToStream(payloads)).ConfigureAwait(false),
                "Duplex" => () => InvokeDuplexAsync(environment.Rpc, payloads),
                _ => throw new InvalidOperationException($"Unknown generated ABI stream shape {shape}.")
            };
            return new GeneratedAbiStreamingCase(
                environment,
                scenario,
                shape,
                itemCount,
                itemBytes,
                invoke);
        }
        catch
        {
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _environment.DisposeAsync();

    private static async ValueTask<long> InvokeServerStreamingAsync(
        IBenchmarkRpc rpc,
        int itemCount,
        int itemBytes)
    {
        long score = 0;
        await foreach (var item in rpc.DownloadPayloadsAsync(itemCount, itemBytes))
        {
            score += BenchmarkRpcService.GetPayloadScore(item);
        }
        return score;
    }

    private static async ValueTask<long> InvokeDuplexAsync(
        IBenchmarkRpc rpc,
        IReadOnlyList<byte[]> payloads)
    {
        long score = 0;
        await foreach (var item in rpc.DuplexPayloadsAsync(BenchmarkEnvironment.ToStream(payloads)))
        {
            score += BenchmarkRpcService.GetPayloadScore(item);
        }
        return score;
    }

    private static (string Shape, int ItemCount, int ItemBytes) GetDimensions(
        GeneratedAbiStreamingScenario scenario) => scenario switch
        {
            GeneratedAbiStreamingScenario.Server1x16 => ("ServerStreaming", 1, 16),
            GeneratedAbiStreamingScenario.Server100x16 => ("ServerStreaming", 100, 16),
            GeneratedAbiStreamingScenario.Server100x4096 => ("ServerStreaming", 100, 4096),
            GeneratedAbiStreamingScenario.Client100x16 => ("ClientStreaming", 100, 16),
            GeneratedAbiStreamingScenario.Client100x4096 => ("ClientStreaming", 100, 4096),
            GeneratedAbiStreamingScenario.Duplex100x16 => ("Duplex", 100, 16),
            GeneratedAbiStreamingScenario.Duplex100x4096 => ("Duplex", 100, 4096),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
}

internal sealed class GeneratedAbiStreamingEvidenceResult
{
    public string Commit { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Shape { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public int ItemBytes { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public bool ServerGc { get; init; }
    public string TieredCompilation { get; init; } = string.Empty;
    public string TieredPgo { get; init; } = string.Empty;
    public int WarmupOperations { get; init; }
    public double RequestedMeasurementSeconds { get; init; }
    public double ActualMeasurementSeconds { get; init; }
    public int Operations { get; init; }
    public double ThroughputOperationsPerSecond { get; init; }
    public double ThroughputItemsPerSecond { get; init; }
    public double FirstCallUs { get; init; }
    public double AverageUs { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
    public double MaxUs { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public double AllocatedBytesPerItem { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int ThreadCount { get; init; }
    public long WorkingSetBytes { get; init; }
    public int ValidationFailures { get; init; }
    public bool HitOperationLimit { get; init; }
}
