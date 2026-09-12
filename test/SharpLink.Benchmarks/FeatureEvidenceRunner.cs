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

internal static class FeatureEvidenceRunner
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
        if (args.Length != 6)
        {
            throw new ArgumentException(
                "Usage: --feature-evidence <server|client> <scenario> " +
                "<warmup-operations> <measurement-seconds> <max-operations> <output-json>");
        }

        var component = args[0].ToLowerInvariant();
        var scenario = args[1];
        var warmupOperations = int.Parse(args[2], CultureInfo.InvariantCulture);
        var measurementSeconds = double.Parse(args[3], CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[4], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[5]);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupOperations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measurementSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);

        await using var benchmark = await CreateCaseAsync(component, scenario).ConfigureAwait(false);
        var firstStarted = Stopwatch.GetTimestamp();
        var firstResult = await benchmark.InvokeAsync().ConfigureAwait(false);
        var firstCallUs = Stopwatch.GetElapsedTime(firstStarted).TotalMicroseconds;
        Validate(firstResult, benchmark.ExpectedResult, component, scenario, "first call");

        for (var operation = 0; operation < warmupOperations; operation++)
        {
            var result = await benchmark.InvokeAsync().ConfigureAwait(false);
            Validate(result, benchmark.ExpectedResult, component, scenario, "warmup");
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
            Validate(result, benchmark.ExpectedResult, component, scenario, "measurement");
            latencies[completed++] = elapsedTicks;
            latencyTicks += elapsedTicks;
        }

        var measurementElapsed = Stopwatch.GetElapsedTime(measurementStarted);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (completed == 0)
            throw new InvalidOperationException("The feature evidence run completed no operations.");

        Array.Sort(latencies, 0, completed);
        var resultDocument = new FeatureEvidenceResult
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Component = component,
            Scenario = scenario,
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
            ThroughputPerSecond = completed / measurementElapsed.TotalSeconds,
            FirstCallUs = firstCallUs,
            AverageUs = TicksToMicroseconds(latencyTicks / (double)completed),
            P50Us = Percentile(latencies, completed, 50),
            P99Us = Percentile(latencies, completed, 99),
            P999Us = Percentile(latencies, completed, 99.9),
            MaxUs = TicksToMicroseconds(latencies[completed - 1]),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)completed,
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
                "Usage: --summarize-feature-evidence <input-directory> <output-markdown> <output-jsonl>");
        }

        var inputDirectory = Path.GetFullPath(args[0]);
        var markdownPath = Path.GetFullPath(args[1]);
        var jsonLinesPath = Path.GetFullPath(args[2]);
        var results = new List<FeatureEvidenceResult>();
        foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.json", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<FeatureEvidenceResult>(
                stream,
                SJsonOptions).ConfigureAwait(false);
            if (result is not null)
                results.Add(result);
        }
        if (results.Count == 0)
            throw new InvalidOperationException("No feature evidence JSON files were found.");

        var markdown = new StringBuilder();
        markdown.AppendLine("# P2-00P Client/Server feature baseline");
        markdown.AppendLine();
        markdown.AppendLine($"- Commit: `{SingleValue(results.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Host: `{SingleValue(results.Select(static item => item.HostName))}`");
        markdown.AppendLine($"- Runtime: `{SingleValue(results.Select(static item => item.RuntimeVersion))}`");
        markdown.AppendLine($"- OS/architecture: `{SingleValue(results.Select(static item => $"{item.OperatingSystem} / {item.Architecture}"))}`");
        markdown.AppendLine($"- Tiered compilation / PGO: `{SingleValue(results.Select(static item => $"{item.TieredCompilation}/{item.TieredPgo}"))}`");
        markdown.AppendLine($"- Raw feature runs: {results.Count}; validation failures: {results.Sum(static item => item.ValidationFailures)}");
        markdown.AppendLine();
        AppendTable(markdown, results, "server", ServerFeatureScenario.StaticDefault.ToString());
        AppendTable(markdown, results, "client", ClientFeatureScenario.FixedDefault.ToString());
        markdown.AppendLine("## Interpretation constraints");
        markdown.AppendLine();
        markdown.AppendLine("- Results are advisory baseline evidence, not a production-code base/head claim; P2-00P changes measurement code only.");
        markdown.AppendLine("- The metrics listener enables the shared SharpLink meter, so the metrics scenario intentionally measures combined client and server instrumentation.");
        markdown.AppendLine("- One-percent tracing uses the real ActivityListener sampling path; non-recorded calls retain propagation behavior.");
        markdown.AppendLine("- CPU/op is aggregate process CPU for the in-process client and server. Allocated B/op is process-wide managed allocation delta.");
        markdown.AppendLine("- Per-request percentiles come from a preallocated, single-worker recorder and are separate from BenchmarkDotNet iteration statistics.");
        markdown.AppendLine("- Raw BenchmarkDotNet, JIT disassembly, environment, and hardware-counter artifacts are retained with the isolated task checkout.");

        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonLinesPath)!);
        await File.WriteAllTextAsync(markdownPath, markdown.ToString()).ConfigureAwait(false);
        var jsonLines = string.Join(
            Environment.NewLine,
            results
                .OrderBy(static item => item.Component, StringComparer.Ordinal)
                .ThenBy(static item => item.Scenario, StringComparer.Ordinal)
                .ThenBy(static item => item.TimestampUtc)
                .Select(static item => JsonSerializer.Serialize(item, SJsonLinesOptions)));
        await File.WriteAllTextAsync(jsonLinesPath, jsonLines + Environment.NewLine).ConfigureAwait(false);
    }

    private static async Task<FeatureBenchmarkCase> CreateCaseAsync(string component, string scenario)
        => component switch
        {
            "server" => await FeatureBenchmarkCase.CreateAsync(
                Enum.Parse<ServerFeatureScenario>(scenario, ignoreCase: true)).ConfigureAwait(false),
            "client" => await FeatureBenchmarkCase.CreateAsync(
                Enum.Parse<ClientFeatureScenario>(scenario, ignoreCase: true)).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Expected server or client.")
        };

    private static void AppendTable(
        StringBuilder markdown,
        IReadOnlyList<FeatureEvidenceResult> results,
        string component,
        string baselineScenario)
    {
        var groups = results
            .Where(item => string.Equals(item.Component, component, StringComparison.Ordinal))
            .GroupBy(static item => item.Scenario, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var baseline = groups.Single(group => group.Key == baselineScenario).ToArray();
        var baselineQps = Median(baseline.Select(static item => item.ThroughputPerSecond));

        markdown.AppendLine($"## {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(component)} scenarios");
        markdown.AppendLine();
        markdown.AppendLine("| Scenario | Runs | QPS median | vs baseline | P50 us | P99 us | P99.9 us | CPU us/op | Alloc B/op | First call us |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in groups)
        {
            var items = group.ToArray();
            var qps = Median(items.Select(static item => item.ThroughputPerSecond));
            markdown.AppendLine(
                $"| {group.Key} | {items.Length} | {qps:F0} | {(qps / baselineQps - 1) * 100:+0.0;-0.0;0.0}% | " +
                $"{Median(items.Select(static item => item.P50Us)):F2} | " +
                $"{Median(items.Select(static item => item.P99Us)):F2} | " +
                $"{Median(items.Select(static item => item.P999Us)):F2} | " +
                $"{Median(items.Select(static item => item.CpuUsPerOperation)):F2} | " +
                $"{Median(items.Select(static item => item.AllocatedBytesPerOperation)):F0} | " +
                $"{Median(items.Select(static item => item.FirstCallUs)):F2} |");
        }
        markdown.AppendLine();
    }

    private static string SingleValue(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? distinct[0] : string.Join(", ", distinct);
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
        var rank = Math.Clamp(
            (int)Math.Ceiling(percentile / 100 * count) - 1,
            0,
            count - 1);
        return TicksToMicroseconds(values[rank]);
    }

    private static double TicksToMicroseconds(double ticks)
        => ticks * 1_000_000d / Stopwatch.Frequency;

    private static void Validate(
        int actual,
        int expected,
        string component,
        string scenario,
        string phase)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{component}/{scenario} returned {actual} instead of {expected} during {phase}.");
        }
    }
}

internal sealed class FeatureEvidenceResult
{
    public string Commit { get; init; } = string.Empty;
    public string Component { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
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
    public double ThroughputPerSecond { get; init; }
    public double FirstCallUs { get; init; }
    public double AverageUs { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
    public double MaxUs { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int ThreadCount { get; init; }
    public long WorkingSetBytes { get; init; }
    public int ValidationFailures { get; init; }
    public bool HitOperationLimit { get; init; }
}
