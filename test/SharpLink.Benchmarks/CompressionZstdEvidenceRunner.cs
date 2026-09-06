using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>Issue #430 feasibility and performance evidence for the official Zstandard profile.</summary>
public static class CompressionZstdEvidenceRunner
{
    private static readonly int[] SFullPayloadSizes = [4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024];
    private static readonly int[] SWanPayloadSizes = [64 * 1024, 256 * 1024, 1024 * 1024];
    private static readonly int[] SFullConcurrency = [1, 8, 32, 128];
    private static readonly int[] SWanConcurrency = [8, 32];
    private static readonly string[] SPatterns = ["dto", "mixed", "random"];
    private static readonly string[] SDirectShapes = ["contiguous", "segmented"];
    private const int DirectMaxOutputBytes = SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes - sizeof(uint);
    private const int BalancedSendQueueBytes = 8 * 1024 * 1024;

    public static async Task RunAsync(string[] args)
    {
        if (args.Length is < 1 or > 2)
            throw new ArgumentException("Usage: --zstd-evidence <output-json> [full|wan]");

        var outputPath = Path.GetFullPath(args[0]);
        var profile = args.Length == 2 ? args[1].ToLowerInvariant() : "full";
        if (profile is not ("full" or "wan"))
            throw new ArgumentException("Evidence profile must be 'full' or 'wan'.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var direct = profile == "full" ? RunDirectMatrix() : [];
        var rpc = await RunRpcMatrixAsync(profile).ConfigureAwait(false);
        var document = new CompressionZstdEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            Profile = profile,
            NetworkProfile = Environment.GetEnvironmentVariable("SHARPLINK_EVIDENCE_NETWORK") ?? "local",
            WireProfile = SharpLinkZstdCompressionProvider.Profile,
            WindowLog2 = SharpLinkZstdCompressionProvider.WindowLog2,
            Direct = direct,
            Rpc = rpc,
            Notes =
            [
                "RPC CPU/allocation measurements are process-wide and include both client and server plus the fixed benchmark harness.",
                "Disabled baselines are content-pattern independent because byte[] serialization preserves the same payload size; one disabled row is recorded per transport/size/concurrency.",
                "Zstd candidate acceptance uses the runtime evidence configuration MinimumPayloadBytes=0, MinimumSavingsBytes=0, MinimumSavingsRatio=0, so a candidate is accepted exactly when compressedBytes + 4 < originalBytes; incompressible candidates are measured and recorded as raw fallback rather than treated as failures.",
                "Direct compression uses the default 4 MiB frame-output budget instead of limiting output to the original payload length, matching Runtime candidate semantics before the adaptive savings decision.",
                "RPC Concurrency is the requested matrix target. EffectiveConcurrency is conservatively capped so original in-flight request bytes use at most 75% of the default Balanced 8 MiB send queue; this keeps the evidence on production defaults and records where large-payload concurrency is not stable.",
                "P99.9 is reported for every scenario, but large-payload scenarios have fewer samples; SampleCount is included so percentile resolution is explicit.",
                "Large-payload memory-bandwidth effects are evaluated from payload throughput, CPU/op, allocated B/op, GC collections, and before/after working-set and managed-heap observations.",
                "The WAN profile expects external loopback shaping and records SHARPLINK_EVIDENCE_NETWORK in the evidence document."
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

    private static List<CompressionDirectEvidence> RunDirectMatrix()
    {
        var results = new List<CompressionDirectEvidence>();
        foreach (var size in SFullPayloadSizes)
        {
            foreach (var pattern in SPatterns)
            {
                var payload = CreatePayload(size, pattern);
                foreach (var shape in SDirectShapes)
                    results.Add(MeasureDirect(payload, pattern, shape));
            }
        }
        return results;
    }

    private static CompressionDirectEvidence MeasureDirect(byte[] payload, string pattern, string shape)
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var input = shape == "segmented"
            ? CreateSegmented(payload, 997)
            : new ReadOnlySequence<byte>(payload);
        var compressed = CompressOnce(provider, input, DirectMaxOutputBytes);
        var compressedInput = shape == "segmented"
            ? CreateSegmented(compressed, 113)
            : new ReadOnlySequence<byte>(compressed);
        var iterations = Math.Clamp((32 * 1024 * 1024) / payload.Length, 16, 1024);

        for (var index = 0; index < 4; index++)
        {
            using var warmCompressed = new PooledByteBufferWriter();
            _ = provider.TryCompress(input, warmCompressed, DirectMaxOutputBytes);
            using var warmDecoded = new PooledByteBufferWriter(payload.Length);
            provider.Decompress(compressedInput, warmDecoded, payload.Length);
        }

        var compress = MeasureSynchronous(iterations, payload.Length, () =>
        {
            using var writer = new PooledByteBufferWriter();
            if (!provider.TryCompress(input, writer, DirectMaxOutputBytes))
                throw new InvalidOperationException("Measured Zstd compression candidate unexpectedly did not fit.");
        });
        var decompress = MeasureSynchronous(iterations, payload.Length, () =>
        {
            using var writer = new PooledByteBufferWriter(payload.Length);
            provider.Decompress(compressedInput, writer, payload.Length);
            if (writer.WrittenCount != payload.Length)
                throw new InvalidOperationException("Measured Zstd decompression length mismatch.");
        });

        var envelopeBytes = checked(compressed.Length + sizeof(uint));
        return new CompressionDirectEvidence
        {
            PayloadBytes = payload.Length,
            Pattern = pattern,
            InputShape = shape,
            Iterations = iterations,
            CompressedBytes = compressed.Length,
            EnvelopeBytes = envelopeBytes,
            CandidateAccepted = envelopeBytes < payload.Length,
            WireSavingsPercent = 100d * (payload.Length - Math.Min(payload.Length, envelopeBytes)) / payload.Length,
            CompressionThroughputMiBPerSecond = compress.ThroughputMiBPerSecond,
            CompressionCpuMicrosecondsPerOperation = compress.CpuMicrosecondsPerOperation,
            CompressionAllocatedBytesPerOperation = compress.AllocatedBytesPerOperation,
            DecompressionThroughputMiBPerSecond = decompress.ThroughputMiBPerSecond,
            DecompressionCpuMicrosecondsPerOperation = decompress.CpuMicrosecondsPerOperation,
            DecompressionAllocatedBytesPerOperation = decompress.AllocatedBytesPerOperation
        };
    }

    private static SynchronousMeasurement MeasureSynchronous(int iterations, int payloadBytes, Action operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
            operation();
        watch.Stop();
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var totalMiB = iterations * payloadBytes / (1024d * 1024d);
        return new SynchronousMeasurement(
            totalMiB / Math.Max(watch.Elapsed.TotalSeconds, double.Epsilon),
            (cpuAfter - cpuBefore).TotalMilliseconds * 1000d / iterations,
            (allocatedAfter - allocatedBefore) / (double)iterations);
    }

    private static async Task<List<CompressionRpcEvidence>> RunRpcMatrixAsync(string profile)
    {
        var sizes = profile == "wan" ? SWanPayloadSizes : SFullPayloadSizes;
        var concurrencies = profile == "wan" ? SWanConcurrency : SFullConcurrency;
        var transports = profile == "wan" ? new[] { "tcp" } : new[] { "tcp", "sharedmemory" };
        var results = new List<CompressionRpcEvidence>();

        foreach (var transport in transports)
        {
            await using (var disabled = await CreateEnvironmentAsync(transport, null, null).ConfigureAwait(false))
            {
                foreach (var size in sizes)
                {
                    var payload = CreatePayload(size, "dto");
                    foreach (var concurrency in concurrencies)
                    {
                        results.Add(await MeasureRpcScenarioAsync(
                            disabled.Rpc,
                            transport,
                            "disabled",
                            "pattern-independent",
                            payload,
                            concurrency,
                            clientCompression: null,
                            serverCompression: null).ConfigureAwait(false));
                    }
                }
            }

            var clientCompression = new EvidenceCompressionProvider(new SharpLinkZstdCompressionProvider());
            var serverCompression = new EvidenceCompressionProvider(new SharpLinkZstdCompressionProvider());
            await using var compressed = await CreateEnvironmentAsync(
                transport,
                options => ConfigureCompression(options, serverCompression),
                options => ConfigureCompression(options, clientCompression)).ConfigureAwait(false);
            foreach (var size in sizes)
            {
                foreach (var pattern in SPatterns)
                {
                    var payload = CreatePayload(size, pattern);
                    foreach (var concurrency in concurrencies)
                    {
                        results.Add(await MeasureRpcScenarioAsync(
                            compressed.Rpc,
                            transport,
                            "zstd",
                            pattern,
                            payload,
                            concurrency,
                            clientCompression,
                            serverCompression).ConfigureAwait(false));
                    }
                }
            }
        }
        return results;
    }

    private static Task<BenchmarkEnvironment> CreateEnvironmentAsync(
        string transport,
        Action<SharpLinkRuntimeOptions>? configureServerRuntime,
        Action<SharpLinkRuntimeOptions>? configureClientRuntime)
        => transport switch
        {
            "tcp" => BenchmarkEnvironment.CreateAsync(
                configureServerRuntime: configureServerRuntime,
                configureClientRuntime: configureClientRuntime),
            "sharedmemory" => BenchmarkEnvironment.CreateSharedMemoryAsync(
                configureServerRuntime,
                configureClientRuntime),
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null)
        };

    private static void ConfigureCompression(
        SharpLinkRuntimeOptions options,
        ISharpLinkCompressionProvider provider)
    {
        options.Compression.MinimumPayloadBytes = 0;
        options.Compression.MinimumSavingsBytes = 0;
        options.Compression.MinimumSavingsRatio = 0;
        options.Compression.Providers.Add(provider);
    }

    private static async Task<CompressionRpcEvidence> MeasureRpcScenarioAsync(
        IBenchmarkRpc rpc,
        string transport,
        string compressionMode,
        string pattern,
        byte[] payload,
        int concurrency,
        EvidenceCompressionProvider? clientCompression,
        EvidenceCompressionProvider? serverCompression)
    {
        for (var index = 0; index < 4; index++)
            ValidateEcho(payload, await rpc.EchoBytesAsync(payload).ConfigureAwait(false));

        var effectiveConcurrency = GetEffectiveConcurrency(concurrency, payload.Length);
        var operations = Math.Max(concurrency, Math.Clamp((32 * 1024 * 1024) / payload.Length, 32, 2048));
        var latencies = new long[operations];
        var clientBefore = clientCompression?.Snapshot() ?? default;
        var serverBefore = serverCompression?.Snapshot() ?? default;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        var heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
        var watch = Stopwatch.StartNew();

        var nextOperation = -1;
        var workers = new Task[Math.Min(effectiveConcurrency, operations)];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Run(async () =>
            {
                while (true)
                {
                    var operationIndex = Interlocked.Increment(ref nextOperation);
                    if (operationIndex >= operations)
                        return;
                    var started = Stopwatch.GetTimestamp();
                    var response = await rpc.EchoBytesAsync(payload).ConfigureAwait(false);
                    latencies[operationIndex] = Stopwatch.GetTimestamp() - started;
                    ValidateEcho(payload, response);
                }
            });
        }
        await Task.WhenAll(workers).ConfigureAwait(false);

        watch.Stop();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var workingSetAfter = process.WorkingSet64;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var heapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
        var clientDelta = (clientCompression?.Snapshot() ?? default) - clientBefore;
        var serverDelta = (serverCompression?.Snapshot() ?? default) - serverBefore;
        var compression = clientDelta + serverDelta;
        Array.Sort(latencies);

        return new CompressionRpcEvidence
        {
            Transport = transport,
            CompressionMode = compressionMode,
            Pattern = pattern,
            PayloadBytes = payload.Length,
            Concurrency = concurrency,
            EffectiveConcurrency = effectiveConcurrency,
            SampleCount = operations,
            Qps = operations / Math.Max(watch.Elapsed.TotalSeconds, double.Epsilon),
            PayloadThroughputMiBPerSecond = operations * payload.Length / (1024d * 1024d) / Math.Max(watch.Elapsed.TotalSeconds, double.Epsilon),
            P50Milliseconds = PercentileMilliseconds(latencies, 0.50),
            P99Milliseconds = PercentileMilliseconds(latencies, 0.99),
            P999Milliseconds = PercentileMilliseconds(latencies, 0.999),
            CpuMicrosecondsPerOperation = (cpuAfter - cpuBefore).TotalMilliseconds * 1000d / operations,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)operations,
            WorkingSetBytesBefore = workingSetBefore,
            WorkingSetBytesAfter = workingSetAfter,
            ManagedHeapBytesBefore = heapBefore,
            ManagedHeapBytesAfter = heapAfter,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            CompressionAttempts = compression.Attempts,
            CompressionAccepted = compression.Accepted,
            CompressionRejected = compression.Attempts - compression.Accepted,
            CandidateRejectionRate = compression.Attempts == 0
                ? 0
                : (compression.Attempts - compression.Accepted) / (double)compression.Attempts,
            OriginalBusinessBytesConsidered = compression.OriginalBytes,
            EstimatedWireBusinessBytes = compression.EstimatedWireBytes,
            EstimatedWireSavingsPercent = compression.OriginalBytes == 0
                ? 0
                : 100d * (compression.OriginalBytes - compression.EstimatedWireBytes) / compression.OriginalBytes
        };
    }

    private static int GetEffectiveConcurrency(int requestedConcurrency, int payloadBytes)
    {
        var queueHeadroomBytes = BalancedSendQueueBytes * 3L / 4;
        var byOriginalBytes = Math.Max(1L, queueHeadroomBytes / Math.Max(1, payloadBytes));
        return Math.Min(requestedConcurrency, checked((int)Math.Min(int.MaxValue, byOriginalBytes)));
    }

    private static double PercentileMilliseconds(long[] sortedTicks, double percentile)
    {
        if (sortedTicks.Length == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile * sortedTicks.Length) - 1;
        index = Math.Clamp(index, 0, sortedTicks.Length - 1);
        return sortedTicks[index] * 1000d / Stopwatch.Frequency;
    }

    private static void ValidateEcho(byte[] expected, byte[] actual)
    {
        if (actual.Length != expected.Length ||
            (actual.Length != 0 && (actual[0] != expected[0] || actual[^1] != expected[^1])))
        {
            throw new InvalidOperationException("Compression evidence RPC payload mismatch.");
        }
    }

    private static byte[] CompressOnce(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> input,
        int maxOutputBytes)
    {
        using var writer = new PooledByteBufferWriter();
        if (!provider.TryCompress(input, writer, maxOutputBytes))
            throw new InvalidOperationException("Zstd evidence payload did not fit its candidate bound.");
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] CreatePayload(int size, string pattern)
    {
        var payload = new byte[size];
        switch (pattern)
        {
            case "dto":
                {
                    var token = "{\"id\":12345,\"name\":\"SharpLink\",\"region\":\"ap-northeast-1\",\"enabled\":true,\"tags\":[\"rpc\",\"zstd\"]}"u8;
                    for (var offset = 0; offset < payload.Length; offset += token.Length)
                        token[..Math.Min(token.Length, payload.Length - offset)].CopyTo(payload.AsSpan(offset));
                    break;
                }
            case "mixed":
                {
                    var random = new Random(0x430 + size);
                    var token = "SharpLink|rpc|mixed|payload|"u8;
                    for (var offset = 0; offset < payload.Length; offset += 256)
                    {
                        var block = payload.AsSpan(offset, Math.Min(256, payload.Length - offset));
                        var structured = Math.Min(192, block.Length);
                        for (var inner = 0; inner < structured; inner += token.Length)
                            token[..Math.Min(token.Length, structured - inner)].CopyTo(block[inner..]);
                        if (structured < block.Length)
                            random.NextBytes(block[structured..]);
                    }
                    break;
                }
            case "random":
                new Random(0x5A17 + size).NextBytes(payload);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern, null);
        }
        return payload;
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var segment = new Segment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private readonly record struct SynchronousMeasurement(
        double ThroughputMiBPerSecond,
        double CpuMicrosecondsPerOperation,
        double AllocatedBytesPerOperation);

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal void SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private sealed class EvidenceCompressionProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private long _attempts;
        private long _accepted;
        private long _originalBytes;
        private long _estimatedWireBytes;

        public string WireProfile => inner.WireProfile;

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            var countingOutput = new CountingBufferWriter(output);
            var result = inner.TryCompress(input, countingOutput, maxOutputBytes, cancellationToken);
            var originalBytes = checked((long)input.Length);
            var candidateBytes = checked((long)countingOutput.WrittenBytes + sizeof(uint));
            var accepted = result && candidateBytes < originalBytes;
            Interlocked.Increment(ref _attempts);
            Interlocked.Add(ref _originalBytes, originalBytes);
            Interlocked.Add(ref _estimatedWireBytes, accepted ? candidateBytes : originalBytes);
            if (accepted)
                Interlocked.Increment(ref _accepted);
            return result;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Decompress(input, output, maxOutputBytes, cancellationToken);

        internal CompressionCounterSnapshot Snapshot()
            => new(
                Volatile.Read(ref _attempts),
                Volatile.Read(ref _accepted),
                Volatile.Read(ref _originalBytes),
                Volatile.Read(ref _estimatedWireBytes));
    }

    private sealed class CountingBufferWriter(IBufferWriter<byte> inner) : IBufferWriter<byte>
    {
        internal int WrittenBytes { get; private set; }
        public void Advance(int count)
        {
            inner.Advance(count);
            WrittenBytes = checked(WrittenBytes + count);
        }
        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }

    private readonly record struct CompressionCounterSnapshot(
        long Attempts,
        long Accepted,
        long OriginalBytes,
        long EstimatedWireBytes)
    {
        public static CompressionCounterSnapshot operator -(
            CompressionCounterSnapshot left,
            CompressionCounterSnapshot right)
            => new(
                left.Attempts - right.Attempts,
                left.Accepted - right.Accepted,
                left.OriginalBytes - right.OriginalBytes,
                left.EstimatedWireBytes - right.EstimatedWireBytes);

        public static CompressionCounterSnapshot operator +(
            CompressionCounterSnapshot left,
            CompressionCounterSnapshot right)
            => new(
                left.Attempts + right.Attempts,
                left.Accepted + right.Accepted,
                left.OriginalBytes + right.OriginalBytes,
                left.EstimatedWireBytes + right.EstimatedWireBytes);
    }
}

public sealed class CompressionZstdEvidenceDocument
{
    public string Commit { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string Profile { get; init; } = string.Empty;
    public string NetworkProfile { get; init; } = string.Empty;
    public string WireProfile { get; init; } = string.Empty;
    public int WindowLog2 { get; init; }
    public List<CompressionDirectEvidence> Direct { get; init; } = [];
    public List<CompressionRpcEvidence> Rpc { get; init; } = [];
    public string[] Notes { get; init; } = [];
}

public sealed class CompressionDirectEvidence
{
    public int PayloadBytes { get; init; }
    public string Pattern { get; init; } = string.Empty;
    public string InputShape { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int CompressedBytes { get; init; }
    public int EnvelopeBytes { get; init; }
    public bool CandidateAccepted { get; init; }
    public double WireSavingsPercent { get; init; }
    public double CompressionThroughputMiBPerSecond { get; init; }
    public double CompressionCpuMicrosecondsPerOperation { get; init; }
    public double CompressionAllocatedBytesPerOperation { get; init; }
    public double DecompressionThroughputMiBPerSecond { get; init; }
    public double DecompressionCpuMicrosecondsPerOperation { get; init; }
    public double DecompressionAllocatedBytesPerOperation { get; init; }
}

public sealed class CompressionRpcEvidence
{
    public string Transport { get; init; } = string.Empty;
    public string CompressionMode { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public int Concurrency { get; init; }
    public int EffectiveConcurrency { get; init; }
    public int SampleCount { get; init; }
    public double Qps { get; init; }
    public double PayloadThroughputMiBPerSecond { get; init; }
    public double P50Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }
    public double P999Milliseconds { get; init; }
    public double CpuMicrosecondsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public long WorkingSetBytesBefore { get; init; }
    public long WorkingSetBytesAfter { get; init; }
    public long ManagedHeapBytesBefore { get; init; }
    public long ManagedHeapBytesAfter { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long CompressionAttempts { get; init; }
    public long CompressionAccepted { get; init; }
    public long CompressionRejected { get; init; }
    public double CandidateRejectionRate { get; init; }
    public long OriginalBusinessBytesConsidered { get; init; }
    public long EstimatedWireBusinessBytes { get; init; }
    public double EstimatedWireSavingsPercent { get; init; }
}
