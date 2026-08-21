using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class CompressionAcceptedPathEvidenceRunner
{
    private static readonly int[] SPayloadSizes = [1024, 65_536, 1_048_576];
    private static readonly SharpLinkCallOptions SMetadataOptions = new()
    {
        Metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "perf-evidence"),
            new KeyValuePair<string, string>("source", "issue-244"))
    };
    private static readonly JsonSerializerOptions SJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException(
                "Usage: --compression-accepted-path-evidence <measurement-seconds> <max-operations> <output-json>");
        }

        var measurementSeconds = double.Parse(args[0], CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[1], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[2]);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measurementSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);

        var document = new CompressionAcceptedPathEvidenceDocument
        {
            Label = Environment.GetEnvironmentVariable("SHARPLINK_PERF_LABEL") ?? "unknown",
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            TimestampUtc = DateTimeOffset.UtcNow,
            HostName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            MeasurementSeconds = measurementSeconds
        };

        document.Results.Add(await MeasureScenarioAsync(
            scenario: "UncompressedDefaultSmall",
            payloadBytes: 0,
            compression: false,
            cancellableMetadata: false,
            admissionImmediate: false,
            disableDefaultRequestTimeout: false,
            measurementSeconds,
            maxOperations).ConfigureAwait(false));

        foreach (var payloadBytes in SPayloadSizes)
        {
            document.Results.Add(await MeasureScenarioAsync(
                scenario: "CompressedDefaultNonCancellable",
                payloadBytes,
                compression: true,
                cancellableMetadata: false,
                admissionImmediate: false,
                disableDefaultRequestTimeout: false,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
            document.Results.Add(await MeasureScenarioAsync(
                scenario: "CompressedNonCancellableNoDefaultTimeout",
                payloadBytes,
                compression: true,
                cancellableMetadata: false,
                admissionImmediate: false,
                disableDefaultRequestTimeout: true,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
            document.Results.Add(await MeasureScenarioAsync(
                scenario: "CompressedCancellableMetadata",
                payloadBytes,
                compression: true,
                cancellableMetadata: true,
                admissionImmediate: false,
                disableDefaultRequestTimeout: false,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
            document.Results.Add(await MeasureScenarioAsync(
                scenario: "CompressedCancellableMetadataNoDefaultTimeout",
                payloadBytes,
                compression: true,
                cancellableMetadata: true,
                admissionImmediate: false,
                disableDefaultRequestTimeout: true,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
            document.Results.Add(await MeasureScenarioAsync(
                scenario: "CompressedCancellableMetadataAdmissionImmediate",
                payloadBytes,
                compression: true,
                cancellableMetadata: true,
                admissionImmediate: true,
                disableDefaultRequestTimeout: false,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(document, SJsonOptions)).ConfigureAwait(false);
        Console.WriteLine($"Accepted compression evidence: {outputPath}");
    }

    internal static async Task SummarizeAsync(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --summarize-compression-accepted-path-evidence <input-directory> <output-markdown>");
        }

        var inputDirectory = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        var documents = new List<CompressionAcceptedPathEvidenceDocument>();
        foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<CompressionAcceptedPathEvidenceDocument>(
                stream,
                SJsonOptions).ConfigureAwait(false);
            if (document is not null)
                documents.Add(document);
        }

        var baseDocuments = documents.Where(static item => item.Label == "base").ToArray();
        var candidateDocuments = documents.Where(static item => item.Label == "candidate").ToArray();
        if (baseDocuments.Length == 0 || candidateDocuments.Length == 0)
            throw new InvalidOperationException("Both base and candidate performance evidence are required.");

        var markdown = new StringBuilder();
        markdown.AppendLine("# Issue #244 accepted-path performance evidence");
        markdown.AppendLine();
        markdown.AppendLine($"- Base commit: `{SingleValue(baseDocuments.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Candidate commit: `{SingleValue(candidateDocuments.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Host: `{SingleValue(documents.Select(static item => item.HostName))}`");
        markdown.AppendLine($"- Runtime: `{SingleValue(documents.Select(static item => item.RuntimeVersion))}`");
        markdown.AppendLine($"- OS/architecture: `{SingleValue(documents.Select(static item => $"{item.OperatingSystem} / {item.Architecture}"))}`");
        markdown.AppendLine($"- Processor count / Server GC: `{SingleValue(documents.Select(static item => $"{item.ProcessorCount}/{item.ServerGc}"))}`");
        markdown.AppendLine($"- Independent repetitions: base={baseDocuments.Length}, candidate={candidateDocuments.Length}");
        markdown.AppendLine();
        markdown.AppendLine("The workflow runs base and candidate on the same Actions runner and alternates run order by repetition. The candidate benchmark harness is overlaid onto the base worktree before building, so measurement code is identical; only product code differs. Compressed cases use Brotli Fastest with thresholds 1 byte / 0 bytes / 0% so the highly-compressible request body is always eligible for compression.");
        markdown.AppendLine();
        markdown.AppendLine("| Scenario | Payload | Base QPS | Candidate QPS | Δ QPS | Base P99 us | Candidate P99 us | Δ P99 | Base CPU us/op | Candidate CPU us/op | Δ CPU | Base B/op | Candidate B/op | Δ B/op |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        var keys = baseDocuments
            .SelectMany(static document => document.Results)
            .Select(static result => (result.Scenario, result.PayloadBytes))
            .Distinct()
            .OrderBy(static key => key.Scenario, StringComparer.Ordinal)
            .ThenBy(static key => key.PayloadBytes)
            .ToArray();
        foreach (var key in keys)
        {
            var baseResults = baseDocuments
                .SelectMany(static document => document.Results)
                .Where(result => result.Scenario == key.Scenario && result.PayloadBytes == key.PayloadBytes)
                .ToArray();
            var candidateResults = candidateDocuments
                .SelectMany(static document => document.Results)
                .Where(result => result.Scenario == key.Scenario && result.PayloadBytes == key.PayloadBytes)
                .ToArray();
            if (baseResults.Length == 0 || candidateResults.Length == 0)
                throw new InvalidOperationException($"Missing comparison data for {key.Scenario}/{key.PayloadBytes}.");

            var baseQps = Median(baseResults.Select(static item => item.ThroughputPerSecond));
            var candidateQps = Median(candidateResults.Select(static item => item.ThroughputPerSecond));
            var baseP99 = Median(baseResults.Select(static item => item.P99Us));
            var candidateP99 = Median(candidateResults.Select(static item => item.P99Us));
            var baseCpu = Median(baseResults.Select(static item => item.CpuUsPerOperation));
            var candidateCpu = Median(candidateResults.Select(static item => item.CpuUsPerOperation));
            var baseAllocated = Median(baseResults.Select(static item => item.AllocatedBytesPerOperation));
            var candidateAllocated = Median(candidateResults.Select(static item => item.AllocatedBytesPerOperation));
            markdown.AppendLine(
                $"| {key.Scenario} | {FormatBytes(key.PayloadBytes)} | {baseQps:F0} | {candidateQps:F0} | {DeltaPercent(baseQps, candidateQps)} | " +
                $"{baseP99:F2} | {candidateP99:F2} | {DeltaPercent(baseP99, candidateP99)} | " +
                $"{baseCpu:F2} | {candidateCpu:F2} | {DeltaPercent(baseCpu, candidateCpu)} | " +
                $"{baseAllocated:F0} | {candidateAllocated:F0} | {DeltaPercent(baseAllocated, candidateAllocated)} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Interpretation");
        markdown.AppendLine();
        markdown.AppendLine("- `UncompressedDefaultSmall` is a tiny non-compressed unary RPC intended to expose fixed dispatch allocation/branch overhead.");
        markdown.AppendLine("- `CompressedDefaultNonCancellable` keeps the normal 30-second client default timeout. That deadline sets the wire Cancellable flag even though the service method is `[NonCancellable]`, so this scenario includes the post-capacity cancellation handoff.");
        markdown.AppendLine("- `CompressedNonCancellableNoDefaultTimeout` disables the client default timeout. With the `[NonCancellable]` service method and no caller cancellation token, it isolates accepted compressed decode/dispatch without the cancellable handoff.");
        markdown.AppendLine("- `CompressedCancellableMetadata` measures the routing-only metadata validation path plus cancellable post-capacity handoff with the normal client default deadline.");
        markdown.AppendLine("- `CompressedCancellableMetadataNoDefaultTimeout` keeps explicit caller cancellation but removes the default deadline, isolating handoff/state cost from deadline-specific work.");
        markdown.AppendLine("- `CompressedCancellableMetadataAdmissionImmediate` measures the synchronous advanced-admission fast path where metadata was already fully parsed before handoff.");
        markdown.AppendLine("- CPU/op and B/op are process-wide deltas for the in-process client/server pair. P99 is per-RPC wall-clock latency at concurrency 1. Positive QPS delta is better; positive P99/CPU/B-op delta is worse.");
        markdown.AppendLine("- These are merge-gate evidence rather than a hard benchmark threshold; hosted-runner noise is reduced by same-host alternating repetitions and medians.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, markdown.ToString()).ConfigureAwait(false);
        Console.WriteLine(markdown.ToString());
    }

    private static async Task<CompressionAcceptedPathEvidenceResult> MeasureScenarioAsync(
        string scenario,
        int payloadBytes,
        bool compression,
        bool cancellableMetadata,
        bool admissionImmediate,
        bool disableDefaultRequestTimeout,
        double measurementSeconds,
        int maxOperations)
    {
        await using var environment = await BenchmarkEnvironment.CreateAsync(
            configureServer: builder =>
            {
                builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
                if (admissionImmediate)
                {
                    builder.UseAdmissionControl(options =>
                    {
                        options.Global.UseConcurrency(4096);
                        options.MaxQueuedCalls = 0;
                        options.MaxQueuedBytes = 0;
                        options.MaxQueueDelay = TimeSpan.Zero;
                    });
                }
            },
            configureServerRuntime: compression ? ConfigureCompression : null,
            configureClientRuntime: compression ? ConfigureCompression : null,
            createClientBuilder: port => CreateBenchmarkClientBuilder(
                port,
                disableDefaultRequestTimeout)).ConfigureAwait(false);

        var payload = payloadBytes == 0 ? Array.Empty<byte>() : CreateCompressiblePayload(payloadBytes);
        using var callerCancellation = new CancellationTokenSource();
        Func<ValueTask<int>> invoke;
        var expected = payloadBytes;
        if (scenario == "UncompressedDefaultSmall")
        {
            expected = 30;
            invoke = () => environment.Rpc.AddAsync(10, 20);
        }
        else if (cancellableMetadata)
        {
            invoke = () => environment.Rpc.ConsumeBytesCancellableAsync(
                payload,
                SMetadataOptions,
                callerCancellation.Token);
        }
        else
        {
            invoke = () => environment.Rpc.ConsumeBytesNonCancellableAsync(payload);
        }

        var firstResult = await invoke().ConfigureAwait(false);
        Validate(firstResult, expected, scenario, "first call");
        var warmupOperations = GetWarmupOperations(payloadBytes);
        for (var operation = 0; operation < warmupOperations; operation++)
        {
            var result = await invoke().ConfigureAwait(false);
            Validate(result, expected, scenario, "warmup");
        }

        var latencies = new long[maxOperations];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        var deadline = started + checked((long)Math.Ceiling(measurementSeconds * Stopwatch.Frequency));
        var completed = 0;
        long totalLatencyTicks = 0;
        while (completed < latencies.Length && Stopwatch.GetTimestamp() < deadline)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            var result = await invoke().ConfigureAwait(false);
            var elapsedTicks = Stopwatch.GetTimestamp() - operationStarted;
            Validate(result, expected, scenario, "measurement");
            latencies[completed++] = elapsedTicks;
            totalLatencyTicks += elapsedTicks;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (completed == 0)
            throw new InvalidOperationException($"No operations completed for {scenario}/{payloadBytes}.");

        Array.Sort(latencies, 0, completed);
        return new CompressionAcceptedPathEvidenceResult
        {
            Scenario = scenario,
            PayloadBytes = payloadBytes,
            Compression = compression ? "brotli-fastest" : "none",
            Admission = admissionImmediate ? "immediate" : "disabled",
            CancellableMetadata = cancellableMetadata,
            ClientDefaultRequestTimeoutDisabled = disableDefaultRequestTimeout,
            Operations = completed,
            ActualMeasurementSeconds = elapsed.TotalSeconds,
            ThroughputPerSecond = completed / elapsed.TotalSeconds,
            AverageUs = TicksToMicroseconds(totalLatencyTicks / (double)completed),
            P50Us = Percentile(latencies, completed, 50),
            P99Us = Percentile(latencies, completed, 99),
            P999Us = Percentile(latencies, completed, 99.9),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)completed,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }

    private static SharpClientBuilder CreateBenchmarkClientBuilder(
        int port,
        bool disableDefaultRequestTimeout)
    {
        var builder = SharpClientBuilder.Create()
            .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
            .UseTcp(IPAddress.Loopback.ToString(), port);
        if (disableDefaultRequestTimeout)
            builder.DisableRequestTimeout();
        return builder;
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
    {
        options.Compression.Providers.Add(
            SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Fastest));
        options.Compression.MinimumPayloadBytes = 1;
        options.Compression.MinimumSavingsBytes = 0;
        options.Compression.MinimumSavingsRatio = 0;
    }

    private static byte[] CreateCompressiblePayload(int length)
    {
        var payload = new byte[length];
        payload.AsSpan().Fill(0x2a);
        return payload;
    }

    private static int GetWarmupOperations(int payloadBytes)
        => payloadBytes switch
        {
            0 => 512,
            <= 1024 => 64,
            <= 65_536 => 16,
            _ => 4
        };

    private static void Validate(int actual, int expected, string scenario, string phase)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{scenario} returned {actual} instead of {expected} during {phase}.");
        }
    }

    private static double Percentile(long[] values, int count, double percentile)
    {
        var rank = Math.Clamp((int)Math.Ceiling(percentile / 100 * count) - 1, 0, count - 1);
        return TicksToMicroseconds(values[rank]);
    }

    private static double TicksToMicroseconds(double ticks)
        => ticks * 1_000_000d / Stopwatch.Frequency;

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

    private static string DeltaPercent(double baseline, double candidate)
        => baseline == 0
            ? "n/a"
            : $"{(candidate / baseline - 1) * 100:+0.0;-0.0;0.0}%";

    private static string FormatBytes(int bytes)
        => bytes switch
        {
            0 => "tiny",
            1024 => "1 KiB",
            65_536 => "64 KiB",
            1_048_576 => "1 MiB",
            _ => bytes.ToString(CultureInfo.InvariantCulture)
        };

    private static string SingleValue(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? distinct[0] : string.Join(", ", distinct);
    }
}

internal sealed class CompressionAcceptedPathEvidenceDocument
{
    public string Label { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public bool ServerGc { get; init; }
    public double MeasurementSeconds { get; init; }
    public List<CompressionAcceptedPathEvidenceResult> Results { get; init; } = [];
}

internal sealed class CompressionAcceptedPathEvidenceResult
{
    public string Scenario { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public string Compression { get; init; } = string.Empty;
    public string Admission { get; init; } = string.Empty;
    public bool CancellableMetadata { get; init; }
    public bool ClientDefaultRequestTimeoutDisabled { get; init; }
    public int Operations { get; init; }
    public double ActualMeasurementSeconds { get; init; }
    public double ThroughputPerSecond { get; init; }
    public double AverageUs { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}
