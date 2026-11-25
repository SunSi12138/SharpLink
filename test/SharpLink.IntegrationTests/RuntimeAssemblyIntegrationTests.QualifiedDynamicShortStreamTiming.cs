using System.Diagnostics;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static async Task MeasureDynamicStreamsAsync(DynamicStreamTimingOptions options,
        QualifiedDynamicStreams streams, Type service, string transport, string interceptor, CancellationToken ct)
    {
        const int warmupSeconds = 11;
        const int minimumCompleted = 100_000;
        const int capacity = 2_000_000;
        var seconds = options.Items == 32 ? 61 : 91;

        var warmupStart = Stopwatch.GetTimestamp();
        do { await streams.ExecuteAsync(options.Family, options.Items, timed: true, ct).ConfigureAwait(false); }
        while (Stopwatch.GetTimestamp() - warmupStart < warmupSeconds * Stopwatch.Frequency);
        var samples = new DynamicStreamObservation[capacity];
        Array.Fill(samples, new DynamicStreamObservation(-1, -1, -1));
        var itemsBefore = GenerationCounter(service, "InputItemsConsumed");
        var exitsBefore = GenerationCounter(service, "InputStreamExits");
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime.Ticks;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var count = 0;
        var start = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            Ensure(count < capacity, "dynamic short-stream sample capacity exceeded");
            samples[count++] = await streams.ExecuteAsync(options.Family, options.Items, timed: true, ct).ConfigureAwait(false);
        }
        while (Stopwatch.GetTimestamp() - start < seconds * Stopwatch.Frequency);
        var elapsed = Stopwatch.GetTimestamp() - start;
        process.Refresh();
        var cpu = process.TotalProcessorTime.Ticks - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var serverItems = GenerationCounter(service, "InputItemsConsumed") - itemsBefore;
        var serverExits = GenerationCounter(service, "InputStreamExits") - exitsBefore;
        var hasInput = options.Family != "s2c";
        Ensure(count >= minimumCompleted && GenerationCounter(service, "Created") == 1 &&
               serverItems == (hasInput ? count * options.Items : 0) && serverExits == (hasInput ? count : 0),
            "dynamic trial must meet its frozen count on the original implementation; input item/exit populations must match");
        RequireNoReplacementTracing();

        using (var raw = new FileStream(Path.ChangeExtension(options.Output, ".streams.bin.gz"), FileMode.CreateNew))
        using (var archive = new GZipStream(raw, CompressionLevel.Fastest))
        using (var buffer = new BufferedStream(archive, 65_536))
        using (var writer = new BinaryWriter(buffer))
        {
            for (var index = 0; index < count; index++)
            {
                writer.Write(samples[index].FirstProgress);
                writer.Write(samples[index].BusinessComplete);
                writer.Write(samples[index].OwnersExited);
            }
        }
        using var report = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(report, new
        {
            Workload = "SharpLink.IntegrationTests.QualifiedDynamicShortStreams",
            SchemaVersion = 1,
            options.SourceCommit,
            options.Family,
            options.Items,
            Transport = transport,
            Interceptor = interceptor,
            RpcTracingEnabled = ReplacementTracingEnabled(),
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            GcLatencyMode = GCSettings.LatencyMode.ToString(),
            StopwatchFrequency = Stopwatch.Frequency,
            WarmupSeconds = warmupSeconds,
            MeasureSeconds = seconds,
            SampleCapacity = capacity,
            ElapsedTicks = elapsed,
            Completed = count,
            ValidatedItems = count * options.Items,
            ServerInputItems = serverItems,
            ServerInputExits = serverExits,
            CpuTimeTicks = cpu,
            ManagedAllocatedBytes = allocated,
            Gen0 = gen0
        }, cancellationToken: ct);
    }

    private sealed record DynamicStreamTimingOptions(string Output, string SourceCommit, string Family, int Items)
    {
        internal static DynamicStreamTimingOptions? FromEnvironment()
        {
            var output = Environment.GetEnvironmentVariable("SHARPLINK_999_DYNAMIC_REPORT");
            var source = Environment.GetEnvironmentVariable("SHARPLINK_999_DYNAMIC_SOURCE");
            var family = Environment.GetEnvironmentVariable("SHARPLINK_999_DYNAMIC_FAMILY");
            var items = Environment.GetEnvironmentVariable("SHARPLINK_999_DYNAMIC_ITEMS");
            if (output is null && source is null && family is null && items is null)
                return null;
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                throw new InvalidOperationException("Dynamic short-stream timing is reserved for Linux GitHub Actions.");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output) ||
                !output.EndsWith(".json", StringComparison.Ordinal) || source is not { Length: 40 } ||
                !source.All(Uri.IsHexDigit) || family is not ("c2s" or "s2c" or "duplex") || items is not ("32" or "256"))
                throw new InvalidOperationException("Dynamic short streams require a frozen report, source, call shape and size.");
            return new(output, source, family, items == "32" ? 32 : 256);
        }
    }
}
