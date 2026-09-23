using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
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
    private const int LocalCodecIterations = 250_000;
    private const int LocalCodecWarmupIterations = 10_000;
    private const int LocalCodecRounds = 6;

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
        foreach (var item in report.LocalCodecCases)
        {
            Console.WriteLine(
                $"{item.Name}: interface {item.InterfaceNanosecondsPerOperation:F2} ns/op, " +
                $"Core {item.CoreNanosecondsPerOperation:F2} ns/op, " +
                $"delta {item.CoreDeltaPercent:+0.00;-0.00;0.00}%, Core {item.CoreSizeBytes} B");
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

            var localCodecCases = RunLocalCodecEvidence();

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
                LocalCodecCases = localCodecCases,
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

    private static List<LocalCodecEvidenceCase> RunLocalCodecEvidence()
    {
#if STATIC_CORE_CANDIDATE
        using var context = new SharpLink.Runtime.SharpLinkRuntimeContextBuilder().Build();

        var codec16Class =
            (global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B)
            context.Codecs.GetCodec<Core16>();
        IRpcCodec<Core16> codec16Interface = codec16Class;
        IRpcSizedCodec<Core16> codec16SizedInterface = codec16Class;
        var codec16Core = codec16Class.StaticCore;

        var snapshotClass =
            (global::SharpLink.Generated.__SharpLinkGeneratedCodec_27DAE40D1F078250)
            context.Codecs.GetCodec<CoreSnapshot>();
        IRpcSizedCodec<CoreSnapshot> snapshotInterface = snapshotClass;
        var snapshotCore = snapshotClass.StaticCore;

        return
        [
            MeasureLocalPair(
                "generated-core16-serialize-varying",
                Unsafe.SizeOf<global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B.Core>(),
                iterations => MeasureCore16SerializeInterface(codec16Interface, iterations, constant: false),
                iterations => MeasureCore16SerializeCore(codec16Core, iterations, constant: false)),
            MeasureLocalPair(
                "generated-core16-serialize-constant",
                Unsafe.SizeOf<global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B.Core>(),
                iterations => MeasureCore16SerializeInterface(codec16Interface, iterations, constant: true),
                iterations => MeasureCore16SerializeCore(codec16Core, iterations, constant: true)),
            MeasureLocalPair(
                "generated-core16-size-varying",
                Unsafe.SizeOf<global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B.Core>(),
                iterations => MeasureCore16SizeInterface(codec16SizedInterface, iterations),
                iterations => MeasureCore16SizeCore(codec16Core, iterations)),
            MeasureLocalPair(
                "generated-snapshot-size-varying",
                Unsafe.SizeOf<global::SharpLink.Generated.__SharpLinkGeneratedCodec_27DAE40D1F078250.Core>(),
                iterations => MeasureSnapshotSizeInterface(snapshotInterface, iterations),
                iterations => MeasureSnapshotSizeCore(snapshotCore, iterations))
        ];
#else
        return [];
#endif
    }

#if STATIC_CORE_CANDIDATE
    private static LocalCodecEvidenceCase MeasureLocalPair(
        string name,
        int coreSizeBytes,
        Func<int, LocalCodecSample> measureInterface,
        Func<int, LocalCodecSample> measureCore)
    {
        _ = measureInterface(LocalCodecWarmupIterations);
        _ = measureCore(LocalCodecWarmupIterations);

        var interfaceSamples = new double[LocalCodecRounds];
        var coreSamples = new double[LocalCodecRounds];
        var interfaceAllocations = new double[LocalCodecRounds];
        var coreAllocations = new double[LocalCodecRounds];
        long expectedChecksum = 0;

        for (var round = 0; round < LocalCodecRounds; round++)
        {
            LocalCodecSample interfaceSample;
            LocalCodecSample coreSample;
            if ((round & 1) == 0)
            {
                interfaceSample = measureInterface(LocalCodecIterations);
                coreSample = measureCore(LocalCodecIterations);
            }
            else
            {
                coreSample = measureCore(LocalCodecIterations);
                interfaceSample = measureInterface(LocalCodecIterations);
            }

            if (interfaceSample.Checksum != coreSample.Checksum)
            {
                throw new InvalidOperationException(
                    $"Local Codec evidence '{name}' produced different interface/Core checksums.");
            }

            expectedChecksum = interfaceSample.Checksum;
            interfaceSamples[round] = interfaceSample.NanosecondsPerOperation;
            coreSamples[round] = coreSample.NanosecondsPerOperation;
            interfaceAllocations[round] = interfaceSample.AllocatedBytesPerOperation;
            coreAllocations[round] = coreSample.AllocatedBytesPerOperation;
        }

        Array.Sort(interfaceSamples);
        Array.Sort(coreSamples);
        Array.Sort(interfaceAllocations);
        Array.Sort(coreAllocations);
        var interfaceMedian = Median(interfaceSamples);
        var coreMedian = Median(coreSamples);

        return new LocalCodecEvidenceCase
        {
            Name = name,
            CoreSizeBytes = coreSizeBytes,
            InterfaceNanosecondsPerOperation = interfaceMedian,
            CoreNanosecondsPerOperation = coreMedian,
            CoreDeltaPercent = (coreMedian / interfaceMedian - 1d) * 100d,
            InterfaceAllocatedBytesPerOperation = Median(interfaceAllocations),
            CoreAllocatedBytesPerOperation = Median(coreAllocations),
            Checksum = expectedChecksum
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureCore16SerializeInterface(
        IRpcCodec<Core16> codec,
        int iterations,
        bool constant)
    {
        using var writer = new SharpLink.Runtime.PooledByteBufferWriter(256);
        return MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.Get16(constant ? 0 : iteration);
            writer.Clear();
            codec.Serialize(in value, writer);
            return unchecked((long)writer.WrittenCount * 31 + value.A);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureCore16SerializeCore(
        global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B.Core codec,
        int iterations,
        bool constant)
    {
        using var writer = new SharpLink.Runtime.PooledByteBufferWriter(256);
        return MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.Get16(constant ? 0 : iteration);
            writer.Clear();
            codec.Serialize(in value, writer);
            return unchecked((long)writer.WrittenCount * 31 + value.A);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureCore16SizeInterface(
        IRpcSizedCodec<Core16> codec,
        int iterations)
        => MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.Get16(iteration);
            if (!codec.TryGetEncodedSize(in value, out var size))
                throw new InvalidOperationException("Core16 must support exact sizing.");
            return unchecked((long)size * 31 + value.A);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureCore16SizeCore(
        global::SharpLink.Generated.__SharpLinkGeneratedCodec_8F3120895402C91B.Core codec,
        int iterations)
        => MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.Get16(iteration);
            if (!codec.TryGetEncodedSize(in value, out var size))
                throw new InvalidOperationException("Core16 Core must support exact sizing.");
            return unchecked((long)size * 31 + value.A);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureSnapshotSizeInterface(
        IRpcSizedCodec<CoreSnapshot> codec,
        int iterations)
        => MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.GetSnapshot(iteration);
            if (!codec.TryGetEncodedSize(in value, out var size, out var snapshot))
                throw new InvalidOperationException("CoreSnapshot must support exact sizing.");
            try
            {
                return unchecked((long)size * 31 + value.Id);
            }
            finally
            {
                codec.ReleaseSnapshot(snapshot);
            }
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalCodecSample MeasureSnapshotSizeCore(
        global::SharpLink.Generated.__SharpLinkGeneratedCodec_27DAE40D1F078250.Core codec,
        int iterations)
        => MeasureLocalLoop(iterations, iteration =>
        {
            var value = EvidencePayloads.GetSnapshot(iteration);
            if (!codec.TryGetEncodedSize(in value, out var size, out var snapshot))
                throw new InvalidOperationException("CoreSnapshot Core must support exact sizing.");
            try
            {
                return unchecked((long)size * 31 + value.Id);
            }
            finally
            {
                codec.ReleaseSnapshot(snapshot);
            }
        });

    private static LocalCodecSample MeasureLocalLoop(int iterations, Func<int, long> operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long checksum = 0;
        var started = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
            checksum = unchecked(checksum * 31 + operation(iteration));
        var elapsed = Stopwatch.GetTimestamp() - started;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        return new LocalCodecSample(
            TicksToNanoseconds(elapsed) / iterations,
            (allocatedAfter - allocatedBefore) / (double)iterations,
            checksum);
    }

    private static double Median(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return (sorted.Length & 1) == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }
#endif

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
        public List<LocalCodecEvidenceCase> LocalCodecCases { get; init; } = [];
        public long Checksum { get; init; }
    }

    private sealed class LocalCodecEvidenceCase
    {
        public string Name { get; init; } = string.Empty;
        public int CoreSizeBytes { get; init; }
        public double InterfaceNanosecondsPerOperation { get; init; }
        public double CoreNanosecondsPerOperation { get; init; }
        public double CoreDeltaPercent { get; init; }
        public double InterfaceAllocatedBytesPerOperation { get; init; }
        public double CoreAllocatedBytesPerOperation { get; init; }
        public long Checksum { get; init; }
    }

#if STATIC_CORE_CANDIDATE
    private readonly record struct LocalCodecSample(
        double NanosecondsPerOperation,
        double AllocatedBytesPerOperation,
        long Checksum);
#endif

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
