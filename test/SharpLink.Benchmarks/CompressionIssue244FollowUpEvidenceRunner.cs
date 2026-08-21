using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Runtime.CompilerServices;
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

internal static class CompressionIssue244FollowUpEvidenceRunner
{
    private static readonly int[] SPayloadSizes = [1024, 65_536, 1_048_576];
    private static readonly SharpLinkCallOptions SMetadataOptions = new()
    {
        Metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "issue-244-follow-up"),
            new KeyValuePair<string, string>("source", "low-ratio-evidence"))
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
                "Usage: --compression-issue244-followup-evidence <measurement-seconds> <max-operations> <output-json>");
        }

        var measurementSeconds = double.Parse(args[0], CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[1], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[2]);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measurementSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);

        var document = new CompressionIssue244FollowUpEvidenceDocument
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

        foreach (var payloadBytes in SPayloadSizes)
        {
            document.Results.Add(await MeasureAcceptedLowRatioAsync(
                payloadBytes,
                admissionImmediate: false,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));
            document.Results.Add(await MeasureAcceptedLowRatioAsync(
                payloadBytes,
                admissionImmediate: true,
                measurementSeconds,
                maxOperations).ConfigureAwait(false));

            foreach (var lowCompressionRatio in new[] { false, true })
            {
                document.Results.Add(await MeasureCapacityRejectedAsync(
                    payloadBytes,
                    lowCompressionRatio,
                    admissionImmediate: false,
                    measurementSeconds,
                    maxOperations).ConfigureAwait(false));
                document.Results.Add(await MeasureCapacityRejectedAsync(
                    payloadBytes,
                    lowCompressionRatio,
                    admissionImmediate: true,
                    measurementSeconds,
                    maxOperations).ConfigureAwait(false));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(document, SJsonOptions)).ConfigureAwait(false);
        Console.WriteLine($"Issue #244 follow-up compression evidence: {outputPath}");
    }

    internal static async Task SummarizeAsync(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --summarize-compression-issue244-followup-evidence <input-directory> <output-markdown>");
        }

        var inputDirectory = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        var documents = new List<CompressionIssue244FollowUpEvidenceDocument>();
        foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<CompressionIssue244FollowUpEvidenceDocument>(
                stream,
                SJsonOptions).ConfigureAwait(false);
            if (document is not null)
                documents.Add(document);
        }

        var baseDocuments = documents.Where(static item => item.Label == "base").ToArray();
        var candidateDocuments = documents.Where(static item => item.Label == "candidate").ToArray();
        if (baseDocuments.Length == 0 || candidateDocuments.Length == 0)
            throw new InvalidOperationException("Both base and candidate follow-up evidence are required.");

        var markdown = new StringBuilder();
        markdown.AppendLine("# Issue #244 follow-up performance evidence");
        markdown.AppendLine();
        markdown.AppendLine($"- Base commit: `{SingleValue(baseDocuments.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Candidate commit: `{SingleValue(candidateDocuments.Select(static item => item.Commit))}`");
        markdown.AppendLine($"- Host: `{SingleValue(documents.Select(static item => item.HostName))}`");
        markdown.AppendLine($"- Runtime: `{SingleValue(documents.Select(static item => item.RuntimeVersion))}`");
        markdown.AppendLine($"- OS/architecture: `{SingleValue(documents.Select(static item => $"{item.OperatingSystem} / {item.Architecture}"))}`");
        markdown.AppendLine($"- Processor count / Server GC: `{SingleValue(documents.Select(static item => $"{item.ProcessorCount}/{item.ServerGc}"))}`");
        markdown.AppendLine($"- Independent repetitions: base={baseDocuments.Length}, candidate={candidateDocuments.Length}");
        markdown.AppendLine();
        markdown.AppendLine("The low-compressibility payload is deterministic xorshift data with every fourth byte cleared. With Brotli Fastest it remains compressible but keeps the compressed wire body close to the original size, exercising the retained-copy cost that the all-0x2a high-compressibility case does not. Capacity-rejected cases hold the sole server call slot with an uncompressed client-streaming call before issuing compressed unary requests.");
        markdown.AppendLine();
        markdown.AppendLine("| Kind | Scenario | Payload | Shape | Admission | Standalone compressed/original | Base QPS | Candidate QPS | Δ QPS | Base P99 us | Candidate P99 us | Δ P99 | Base CPU us/op | Candidate CPU us/op | Δ CPU | Base B/op | Candidate B/op | Δ B/op |");
        markdown.AppendLine("|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        var keys = baseDocuments
            .SelectMany(static document => document.Results)
            .Select(static result => (result.Kind, result.Scenario, result.PayloadBytes, result.PayloadShape, result.Admission))
            .Distinct()
            .OrderBy(static key => key.Kind, StringComparer.Ordinal)
            .ThenBy(static key => key.Scenario, StringComparer.Ordinal)
            .ThenBy(static key => key.PayloadBytes)
            .ThenBy(static key => key.PayloadShape, StringComparer.Ordinal)
            .ThenBy(static key => key.Admission, StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            var baseResults = SelectResults(baseDocuments, key).ToArray();
            var candidateResults = SelectResults(candidateDocuments, key).ToArray();
            if (baseResults.Length == 0 || candidateResults.Length == 0)
                throw new InvalidOperationException($"Missing follow-up comparison data for {key}.");

            var baseQps = Median(baseResults.Select(static item => item.ThroughputPerSecond));
            var candidateQps = Median(candidateResults.Select(static item => item.ThroughputPerSecond));
            var baseP99 = Median(baseResults.Select(static item => item.P99Us));
            var candidateP99 = Median(candidateResults.Select(static item => item.P99Us));
            var baseCpu = Median(baseResults.Select(static item => item.CpuUsPerOperation));
            var candidateCpu = Median(candidateResults.Select(static item => item.CpuUsPerOperation));
            var baseAllocated = Median(baseResults.Select(static item => item.AllocatedBytesPerOperation));
            var candidateAllocated = Median(candidateResults.Select(static item => item.AllocatedBytesPerOperation));
            var ratio = Median(candidateResults.Select(static item => item.StandaloneCompressionRatio));
            markdown.AppendLine(
                $"| {key.Kind} | {key.Scenario} | {FormatBytes(key.PayloadBytes)} | {key.PayloadShape} | {key.Admission} | {ratio:P1} | " +
                $"{baseQps:F0} | {candidateQps:F0} | {DeltaPercent(baseQps, candidateQps)} | " +
                $"{baseP99:F2} | {candidateP99:F2} | {DeltaPercent(baseP99, candidateP99)} | " +
                $"{baseCpu:F2} | {candidateCpu:F2} | {DeltaPercent(baseCpu, candidateCpu)} | " +
                $"{baseAllocated:F0} | {candidateAllocated:F0} | {DeltaPercent(baseAllocated, candidateAllocated)} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Interpretation");
        markdown.AppendLine();
        markdown.AppendLine("- `accepted-low-ratio` covers the default and synchronous advanced-admission cancellable+metadata paths with a retained compressed frame near the original payload size.");
        markdown.AppendLine("- `capacity-rejected` is the resource-amplification case from #244. On the candidate, the provider should not run after capacity is known to be exhausted; the base/candidate CPU/op delta quantifies the avoided work.");
        markdown.AppendLine("- The high-compressibility rejected control uses the original all-0x2a shape; the low-compressibility rejected case shows how the avoided decompression cost changes as compressed wire bytes approach the original size.");
        markdown.AppendLine("- CPU/op and B/op are process-wide deltas for the in-process client/server pair. P99 is per-attempt wall-clock latency at concurrency 1. Positive QPS delta is better; positive P99/CPU/B-op delta is worse.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, markdown.ToString()).ConfigureAwait(false);
        Console.WriteLine(markdown.ToString());
    }

    private static IEnumerable<CompressionIssue244FollowUpEvidenceResult> SelectResults(
        IEnumerable<CompressionIssue244FollowUpEvidenceDocument> documents,
        (string Kind, string Scenario, int PayloadBytes, string PayloadShape, string Admission) key)
        => documents
            .SelectMany(static document => document.Results)
            .Where(result =>
                result.Kind == key.Kind &&
                result.Scenario == key.Scenario &&
                result.PayloadBytes == key.PayloadBytes &&
                result.PayloadShape == key.PayloadShape &&
                result.Admission == key.Admission);

    private static async Task<CompressionIssue244FollowUpEvidenceResult> MeasureAcceptedLowRatioAsync(
        int payloadBytes,
        bool admissionImmediate,
        double measurementSeconds,
        int maxOperations)
    {
        await using var environment = await CreateEnvironmentAsync(
            admissionImmediate,
            capacityOne: false).ConfigureAwait(false);
        var payload = CreateLowCompressibilityPayload(payloadBytes);
        using var callerCancellation = new CancellationTokenSource();

        async ValueTask InvokeAsync()
        {
            var result = await environment.Rpc.ConsumeBytesCancellableAsync(
                payload,
                SMetadataOptions,
                callerCancellation.Token).ConfigureAwait(false);
            if (result != payloadBytes)
                throw new InvalidOperationException($"accepted low-ratio call returned {result} instead of {payloadBytes}.");
        }

        return await MeasureAsync(
            kind: "accepted-low-ratio",
            scenario: "CompressedCancellableMetadataLowRatio",
            payload,
            payloadShape: "low-compressibility",
            admissionImmediate,
            InvokeAsync,
            measurementSeconds,
            maxOperations).ConfigureAwait(false);
    }

    private static async Task<CompressionIssue244FollowUpEvidenceResult> MeasureCapacityRejectedAsync(
        int payloadBytes,
        bool lowCompressionRatio,
        bool admissionImmediate,
        double measurementSeconds,
        int maxOperations)
    {
        await using var environment = await CreateEnvironmentAsync(
            admissionImmediate,
            capacityOne: true).ConfigureAwait(false);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = environment.Rpc.UploadNumbersAsync(HoldCallOpen(releaseBlocker.Task)).AsTask();
        try
        {
            await WaitForCapacityExhaustionAsync(environment.Rpc).ConfigureAwait(false);
            var payload = lowCompressionRatio
                ? CreateLowCompressibilityPayload(payloadBytes)
                : CreateHighCompressibilityPayload(payloadBytes);

            async ValueTask InvokeRejectedAsync()
            {
                try
                {
                    _ = await environment.Rpc.ConsumeBytesNonCancellableAsync(payload).ConfigureAwait(false);
                    throw new InvalidOperationException("capacity-rejected compressed request unexpectedly succeeded.");
                }
                catch (SharpLinkException exception) when (
                    exception.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                }
            }

            return await MeasureAsync(
                kind: "capacity-rejected",
                scenario: "CompressedDefaultCapacityFull",
                payload,
                payloadShape: lowCompressionRatio ? "low-compressibility" : "high-compressibility",
                admissionImmediate,
                InvokeRejectedAsync,
                measurementSeconds,
                maxOperations).ConfigureAwait(false);
        }
        finally
        {
            releaseBlocker.TrySetResult();
            var sum = await blocker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (sum != 1)
                throw new InvalidOperationException($"capacity blocker returned {sum} instead of 1.");
        }
    }

    private static async Task<CompressionIssue244FollowUpEvidenceResult> MeasureAsync(
        string kind,
        string scenario,
        byte[] payload,
        string payloadShape,
        bool admissionImmediate,
        Func<ValueTask> invoke,
        double measurementSeconds,
        int maxOperations)
    {
        var warmupOperations = GetWarmupOperations(payload.Length);
        for (var operation = 0; operation < warmupOperations; operation++)
            await invoke().ConfigureAwait(false);

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
        while (completed < latencies.Length && Stopwatch.GetTimestamp() < deadline)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            await invoke().ConfigureAwait(false);
            latencies[completed++] = Stopwatch.GetTimestamp() - operationStarted;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (completed == 0)
            throw new InvalidOperationException($"No operations completed for {scenario}/{payload.Length}.");

        Array.Sort(latencies, 0, completed);
        return new CompressionIssue244FollowUpEvidenceResult
        {
            Kind = kind,
            Scenario = scenario,
            PayloadBytes = payload.Length,
            PayloadShape = payloadShape,
            Admission = admissionImmediate ? "immediate" : "disabled",
            StandaloneCompressionRatio = GetStandaloneCompressionRatio(payload),
            Operations = completed,
            ThroughputPerSecond = completed / elapsed.TotalSeconds,
            P99Us = Percentile(latencies, completed, 99),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)completed
        };
    }

    private static Task<BenchmarkEnvironment> CreateEnvironmentAsync(
        bool admissionImmediate,
        bool capacityOne)
        => BenchmarkEnvironment.CreateAsync(
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
            configureServerRuntime: options =>
            {
                ConfigureCompression(options);
                if (capacityOne)
                {
                    options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                    options.FlowControl.MaxConcurrentCallsPerServer = 1;
                }
            },
            configureClientRuntime: ConfigureCompression,
            createClientBuilder: port => SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
                .UseConnectionPool(options =>
                {
                    options.MinConnections = 1;
                    options.MaxConnections = 1;
                })
                .UseTcp(IPAddress.Loopback.ToString(), port));

    private static async Task WaitForCapacityExhaustionAsync(IBenchmarkRpc rpc)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                _ = await rpc.ConsumeBytesNonCancellableAsync(Array.Empty<byte>()).ConfigureAwait(false);
            }
            catch (SharpLinkException exception) when (
                exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                return;
            }
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<int> HoldCallOpen(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await release.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
    {
        options.Compression.Providers.Add(
            SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Fastest));
        options.Compression.MinimumPayloadBytes = 1;
        options.Compression.MinimumSavingsBytes = 0;
        options.Compression.MinimumSavingsRatio = 0;
    }

    private static byte[] CreateHighCompressibilityPayload(int length)
    {
        var payload = new byte[length];
        payload.AsSpan().Fill(0x2a);
        return payload;
    }

    private static byte[] CreateLowCompressibilityPayload(int length)
    {
        var payload = new byte[length];
        uint state = 0x9E3779B9u;
        for (var index = 0; index < payload.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[index] = index % 4 == 0 ? (byte)0 : (byte)state;
        }
        return payload;
    }

    private static double GetStandaloneCompressionRatio(byte[] payload)
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Fastest);
        var output = new ArrayBufferWriter<byte>(payload.Length);
        var result = provider.Compress(
            new ReadOnlySequence<byte>(payload),
            output,
            checked(payload.Length * 2),
            CancellationToken.None);
        if (result.ConsumedBytes != payload.Length || result.WrittenBytes != output.WrittenCount)
            throw new InvalidOperationException("Standalone compression provider reported inconsistent byte counts.");
        return result.WrittenBytes / (double)payload.Length;
    }

    private static int GetWarmupOperations(int payloadBytes)
        => payloadBytes switch
        {
            <= 1024 => 32,
            <= 65_536 => 8,
            _ => 2
        };

    private static double Percentile(long[] values, int count, double percentile)
    {
        var rank = Math.Clamp((int)Math.Ceiling(percentile / 100 * count) - 1, 0, count - 1);
        return values[rank] * 1_000_000d / Stopwatch.Frequency;
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

    private static string DeltaPercent(double baseline, double candidate)
    {
        if (baseline == 0)
            return candidate == 0 ? "0.0%" : "n/a";
        return $"{(candidate - baseline) / baseline:P1}";
    }

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
        if (distinct.Length != 1)
            throw new InvalidOperationException($"Expected one value, got: {string.Join(", ", distinct)}");
        return distinct[0];
    }
}

internal sealed class CompressionIssue244FollowUpEvidenceDocument
{
    public string Label { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public bool ServerGc { get; set; }
    public double MeasurementSeconds { get; set; }
    public List<CompressionIssue244FollowUpEvidenceResult> Results { get; set; } = [];
}

internal sealed class CompressionIssue244FollowUpEvidenceResult
{
    public string Kind { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public int PayloadBytes { get; set; }
    public string PayloadShape { get; set; } = string.Empty;
    public string Admission { get; set; } = string.Empty;
    public double StandaloneCompressionRatio { get; set; }
    public int Operations { get; set; }
    public double ThroughputPerSecond { get; set; }
    public double P99Us { get; set; }
    public double CpuUsPerOperation { get; set; }
    public double AllocatedBytesPerOperation { get; set; }
}
