using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.StaticCodecCoreEvidence;

internal static partial class Program
{
    private const int DefaultCallIterations = 512;
    // Keep the same-source A/B transport under the dispatcher's bounded element queue so
    // long SharedMemory streams exercise negotiated flow-credit backpressure instead of the safety cap.
    private const int EvidenceStreamWindowBytes = 128 * 1024;
    private const int EvidenceConnectionWindowBytes = 4 * 1024 * 1024;
    private static readonly int[] DefaultStreamLengths = [1, 8, 64, 1_000, 10_000];
    private static readonly int[] FullStreamLengths = [1, 8, 64, 1_000, 10_000, 100_000];
    private static readonly int[] ConcurrencyLevels = [1, 8, 32, 128];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return VerifyManifest();

        if (!string.Equals(args[0], "--rpc-evidence", StringComparison.Ordinal))
            throw new ArgumentException("Expected --rpc-evidence [output.json] [--full].");

        var output = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : Path.Combine("artifacts", "performance", "static-codec-core-evidence.json");
        var full = args.Any(static arg => string.Equals(arg, "--full", StringComparison.Ordinal));

        var report = await RunRpcEvidenceAsync(full).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(report, EvidenceJsonContext.Default.EvidenceReport))
            .ConfigureAwait(false);

        Console.WriteLine($"static Codec Core RPC evidence written to {output}");
        foreach (var item in report.Cases)
        {
            Console.WriteLine(
                $"{item.Name}: {item.NanosecondsPerUnit:F2} ns/unit, " +
                $"{item.CpuNanosecondsPerUnit:F2} CPU ns/unit, " +
                $"{item.AllocatedBytesPerUnit:F2} B/unit, p99 {item.P99Nanoseconds:F0} ns");
        }
        Console.WriteLine($"checksum={report.Checksum}");
        return 0;
    }

    private static void ConfigureEvidenceRuntime(SharpLink.Runtime.SharpLinkRuntimeOptions options)
    {
        options.FlowControl.StreamReceiveWindowBytes = EvidenceStreamWindowBytes;
        options.FlowControl.ConnectionReceiveWindowBytes = EvidenceConnectionWindowBytes;
    }

    private static int VerifyManifest()
    {
        var owner = typeof(IStaticCodecCoreEvidenceRpc).Assembly;
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .SingleOrDefault(candidate => ReferenceEquals(candidate.OwnerAssembly, owner));

        if (manifest is null)
            throw new InvalidOperationException("Static Codec Core evidence manifest was not registered.");

        if (manifest.ApiVersion != SharpLinkGeneratedManifestVersions.Api ||
            manifest.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
        {
            throw new InvalidOperationException(
                $"Generated manifest version mismatch: API {manifest.ApiVersion}, Protocol {manifest.ProtocolVersion}.");
        }

        Console.WriteLine(
            $"SharpLink static Codec Core evidence: API {manifest.ApiVersion}, Protocol {manifest.ProtocolVersion}, ABI {SharpLinkGeneratedManifestVersions.AbiIdentity}");
        return 0;
    }

    private static async Task<EvidenceReport> RunRpcEvidenceAsync(bool full)
    {
        var transportName = $"sharplink-static-core-{Guid.NewGuid():N}";
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(full ? 20 : 8));
        await using var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(transportName)
            .UseRuntime(ConfigureEvidenceRuntime)
            .Build();
        await server.StartAsync(lifetime.Token).ConfigureAwait(false);

        await using var client = SharpClientBuilder.Create()
            .UseSharedMemory(transportName)
            .UseRequestTimeout(TimeSpan.FromSeconds(60))
            .UseRuntime(ConfigureEvidenceRuntime)
            .Build();

        try
        {
            await client.ConnectAsync(lifetime.Token).ConfigureAwait(false);
            var rpc = client.Get<IStaticCodecCoreEvidenceRpc>();
            await WarmupAsync(rpc).ConfigureAwait(false);

            var cases = new List<EvidenceCase>();
            long checksum = 0;
            var callIterations = full ? 2_048 : DefaultCallIterations;

            cases.Add(await MeasureAsync("unary-tiny", callIterations, 1, async i =>
            {
                var result = await rpc.TinyAsync(i).ConfigureAwait(false);
                return result;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-dto-16", callIterations, 1, async i =>
            {
                var result = await rpc.Echo16Async(EvidencePayloads.Get16(i)).ConfigureAwait(false);
                return result.A;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-dto-64", callIterations, 1, async i =>
            {
                var result = await rpc.Echo64Async(EvidencePayloads.Get64(i)).ConfigureAwait(false);
                return result.A.A;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-dto-256", Math.Max(128, callIterations / 4), 1, async i =>
            {
                var result = await rpc.Echo256Async(EvidencePayloads.Get256(i)).ConfigureAwait(false);
                return result.A.A.A;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-string", Math.Max(128, callIterations / 2), 1, async i =>
            {
                var result = await rpc.EchoTextAsync(EvidencePayloads.GetText(i)).ConfigureAwait(false);
                return result.Id + result.Text.Length;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-collection", Math.Max(128, callIterations / 2), 1, async i =>
            {
                var result = await rpc.EchoCollectionAsync(EvidencePayloads.GetCollection(i)).ConfigureAwait(false);
                return result.Id + result.Values.Count;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-exact-snapshot", Math.Max(128, callIterations / 2), 1, async i =>
            {
                var result = await rpc.EchoSnapshotAsync(EvidencePayloads.GetSnapshot(i)).ConfigureAwait(false);
                return result.Id + result.Nested.A.A;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("unary-mixed-concrete-fallback", Math.Max(128, callIterations / 2), 1, async i =>
            {
                var result = await rpc.EchoMixedAsync(EvidencePayloads.GetMixed(i)).ConfigureAwait(false);
                return result.Concrete.A.A + result.Fallback.Length;
            }).ConfigureAwait(false));

            cases.Add(await MeasureAsync("oneway-dto-64", callIterations, 1, async i =>
            {
                await rpc.Publish64Async(EvidencePayloads.Get64(i)).ConfigureAwait(false);
                return i;
            }).ConfigureAwait(false));

            foreach (var concurrency in ConcurrencyLevels)
            {
                var batches = full ? Math.Max(8, 2_048 / concurrency) : Math.Max(4, 512 / concurrency);
                cases.Add(await MeasureAsync($"unary-tiny-c{concurrency}", batches, concurrency, i =>
                    RunConcurrentTinyAsync(rpc, concurrency, i)).ConfigureAwait(false));
            }

            foreach (var itemCount in full ? FullStreamLengths : DefaultStreamLengths)
            {
                var streamIterations = itemCount >= 10_000 ? 2 : itemCount >= 1_000 ? 4 : 16;

                cases.Add(await MeasureAsync($"client-stream-{itemCount}", streamIterations, itemCount, async iteration =>
                {
                    var result = await rpc.Upload64Async(Stream64(itemCount, iteration)).ConfigureAwait(false);
                    return result.A.A + itemCount;
                }).ConfigureAwait(false));

                cases.Add(await MeasureAsync($"server-stream-{itemCount}", streamIterations, itemCount, async iteration =>
                {
                    long local = 0;
                    var received = 0;
                    await foreach (var item in rpc.Download64Async(itemCount).ConfigureAwait(false))
                    {
                        local += item.A.A;
                        received++;
                    }
                    if (received != itemCount)
                        throw new InvalidOperationException($"Expected {itemCount} server-stream items, got {received}.");
                    return local + iteration;
                }).ConfigureAwait(false));

                cases.Add(await MeasureAsync($"duplex-stream-{itemCount}", streamIterations, itemCount, async iteration =>
                {
                    long local = 0;
                    var received = 0;
                    await foreach (var item in rpc.Duplex64Async(Stream64(itemCount, iteration)).ConfigureAwait(false))
                    {
                        local += item.A.A;
                        received++;
                    }
                    if (received != itemCount)
                        throw new InvalidOperationException($"Expected {itemCount} duplex items, got {received}.");
                    return local;
                }).ConfigureAwait(false));
            }

            foreach (var item in cases)
                checksum = unchecked(checksum * 31 + item.Checksum);

            return new EvidenceReport
            {
                Runtime = RuntimeInformation.FrameworkDescription,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                GcServer = GCSettings.IsServerGC,
                ProcessorCount = Environment.ProcessorCount,
                StopwatchFrequency = Stopwatch.Frequency,
                Full = full,
                Cases = cases,
                Checksum = checksum
            };
        }
        finally
        {
            await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await server.WaitForShutdownAsync().ConfigureAwait(false);
        }
    }

    private static async Task WarmupAsync(IStaticCodecCoreEvidenceRpc rpc)
    {
        for (var i = 0; i < 64; i++)
        {
            _ = await rpc.TinyAsync(i).ConfigureAwait(false);
            _ = await rpc.Echo64Async(EvidencePayloads.Get64(i)).ConfigureAwait(false);
        }

        _ = await rpc.Upload64Async(Stream64(8, 0)).ConfigureAwait(false);
        await foreach (var _ in rpc.Download64Async(8).ConfigureAwait(false))
        {
        }
        await foreach (var _ in rpc.Duplex64Async(Stream64(8, 1)).ConfigureAwait(false))
        {
        }
    }

    private static async ValueTask<long> RunConcurrentTinyAsync(
        IStaticCodecCoreEvidenceRpc rpc,
        int concurrency,
        int iteration)
    {
        var tasks = new Task<int>[concurrency];
        for (var index = 0; index < concurrency; index++)
            tasks[index] = rpc.TinyAsync(unchecked(iteration * concurrency + index)).AsTask();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        long checksum = 0;
        foreach (var result in results)
            checksum += result;
        return checksum;
    }

    private static async IAsyncEnumerable<Core64> Stream64(int count, int seed)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        for (var index = 0; index < count; index++)
            yield return EvidencePayloads.Get64(index + seed);
    }

    private static async Task<EvidenceCase> MeasureAsync(
        string name,
        int iterations,
        int unitsPerIteration,
        Func<int, ValueTask<long>> action)
    {
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        if (unitsPerIteration <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsPerIteration));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var latencies = new long[iterations];
        long checksum = 0;

        var totalStart = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var start = Stopwatch.GetTimestamp();
            checksum = unchecked(checksum * 31 + await action(iteration).ConfigureAwait(false));
            latencies[iteration] = Stopwatch.GetTimestamp() - start;
        }
        var totalTicks = Stopwatch.GetTimestamp() - totalStart;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var cpuAfter = process.TotalProcessorTime;

        Array.Sort(latencies);
        var units = checked((long)iterations * unitsPerIteration);
        var elapsedNs = TicksToNanoseconds(totalTicks);
        var cpuNs = (cpuAfter - cpuBefore).TotalMilliseconds * 1_000_000d;

        return new EvidenceCase
        {
            Name = name,
            Iterations = iterations,
            Units = units,
            NanosecondsPerUnit = elapsedNs / units,
            CpuNanosecondsPerUnit = cpuNs / units,
            AllocatedBytesPerUnit = (allocatedAfter - allocatedBefore) / (double)units,
            P50Nanoseconds = TicksToNanoseconds(Percentile(latencies, 0.50)),
            P99Nanoseconds = TicksToNanoseconds(Percentile(latencies, 0.99)),
            P999Nanoseconds = TicksToNanoseconds(Percentile(latencies, 0.999)),
            Checksum = checksum
        };
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double TicksToNanoseconds(long ticks)
        => ticks * (1_000_000_000d / Stopwatch.Frequency);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(EvidenceReport))]
    private sealed partial class EvidenceJsonContext : JsonSerializerContext
    {
    }

    private sealed class EvidenceReport
    {
        public string Runtime { get; init; } = string.Empty;
        public string RuntimeIdentifier { get; init; } = string.Empty;
        public string ProcessArchitecture { get; init; } = string.Empty;
        public bool GcServer { get; init; }
        public int ProcessorCount { get; init; }
        public long StopwatchFrequency { get; init; }
        public bool Full { get; init; }
        public List<EvidenceCase> Cases { get; init; } = [];
        public long Checksum { get; init; }
    }

    private sealed class EvidenceCase
    {
        public string Name { get; init; } = string.Empty;
        public int Iterations { get; init; }
        public long Units { get; init; }
        public double NanosecondsPerUnit { get; init; }
        public double CpuNanosecondsPerUnit { get; init; }
        public double AllocatedBytesPerUnit { get; init; }
        public double P50Nanoseconds { get; init; }
        public double P99Nanoseconds { get; init; }
        public double P999Nanoseconds { get; init; }
        public long Checksum { get; init; }
    }
}
