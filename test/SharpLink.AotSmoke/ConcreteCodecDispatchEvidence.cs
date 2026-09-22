using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.AotSmoke;

internal static class ConcreteCodecDispatchEvidence
{
    private static long s_sink;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine(
                "Usage: --concrete-codec-evidence <warmup-ops> <rpc-ops> <codec-ops> <samples> <output-json>");
            return 2;
        }

        var warmupOperations = ParsePositive(args[0], "warmup-ops");
        var rpcOperations = ParsePositive(args[1], "rpc-ops");
        var codecOperations = ParsePositive(args[2], "codec-ops");
        var sampleCount = ParsePositive(args[3], "samples");
        var outputPath = Path.GetFullPath(args[4]);

        try
        {
            var report = await RunCoreAsync(
                warmupOperations,
                rpcOperations,
                codecOperations,
                sampleCount).ConfigureAwait(false);
            WriteReport(outputPath, report);
            Console.WriteLine(await File.ReadAllTextAsync(outputPath).ConfigureAwait(false));
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<EvidenceReport> RunCoreAsync(
        int warmupOperations,
        int rpcOperations,
        int codecOperations,
        int sampleCount)
    {
        var sharedMemoryName = $"sharplink-codec-evidence-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(sharedMemoryName)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunUntilStoppedAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        await using var client = SharpClientBuilder.Create()
            .UseSharedMemory(sharedMemoryName)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .DisableRequestTimeout()
            .Build();

        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var rpc = client.Get<IConcreteCodecEvidenceRpc>();
            var runtimeContext = ((IRpcChannel)client).RuntimeContext;
            var provider = runtimeContext.Codecs;

            var small = new ConcreteCodecSmallPayload
            {
                Value = 42,
                Timestamp = 0x1020304050607080L
            };
            var nested = new ConcreteCodecNestedPayload
            {
                Primary = small,
                Items =
                [
                    new ConcreteCodecSmallPayload { Value = 1, Timestamp = 11 },
                    new ConcreteCodecSmallPayload { Value = 2, Timestamp = 22 },
                    new ConcreteCodecSmallPayload { Value = 3, Timestamp = 33 },
                    new ConcreteCodecSmallPayload { Value = 4, Timestamp = 44 }
                ]
            };

            var smallCodec = provider.GetCodec<ConcreteCodecSmallPayload>();
            var nestedCodec = provider.GetCodec<ConcreteCodecNestedPayload>();
            var smallBytes = SerializeOnce(smallCodec, small, runtimeContext.Buffers);
            var nestedBytes = SerializeOnce(nestedCodec, nested, runtimeContext.Buffers);
            var nestedSequence = new ReadOnlySequence<byte>(nestedBytes);
            var writer = runtimeContext.Buffers.Rent();
            try
            {
                var cases = new List<EvidenceCase>
                {
                    await MeasureAsyncCase(
                        "small-generated-dto-unary",
                        warmupOperations,
                        rpcOperations,
                        sampleCount,
                        async () =>
                        {
                            var result = await rpc.EchoSmallAsync(small).ConfigureAwait(false);
                            if (result.Value != small.Value || result.Timestamp != small.Timestamp)
                                throw new InvalidOperationException("small unary result mismatch");
                            Interlocked.Add(ref s_sink, result.Value);
                        }).ConfigureAwait(false),
                    await MeasureAsyncCase(
                        "nested-generated-dto-unary",
                        warmupOperations,
                        rpcOperations,
                        sampleCount,
                        async () =>
                        {
                            var result = await rpc.EchoNestedAsync(nested).ConfigureAwait(false);
                            if (result.Primary.Value != 42 || result.Items.Count != 4)
                                throw new InvalidOperationException("nested unary result mismatch");
                            Interlocked.Add(ref s_sink, result.Items[3].Value);
                        }).ConfigureAwait(false),
                    await MeasureOneWayAsync(
                        rpc,
                        small,
                        warmupOperations,
                        rpcOperations,
                        sampleCount).ConfigureAwait(false),
                    MeasureSyncCase(
                        "response-serialize-nested-generated-dto",
                        warmupOperations * 4,
                        codecOperations,
                        sampleCount,
                        () =>
                        {
                            writer.Clear();
                            nestedCodec.Serialize(nested, writer);
                            Interlocked.Add(ref s_sink, writer.WrittenCount);
                        }),
                    MeasureSyncCase(
                        "request-deserialize-nested-generated-dto",
                        warmupOperations * 4,
                        codecOperations,
                        sampleCount,
                        () =>
                        {
                            var value = nestedCodec.Deserialize(in nestedSequence)
                                ?? throw new InvalidOperationException("nested decode returned null");
                            Interlocked.Add(ref s_sink, value.Primary.Value + value.Items.Count);
                        })
                };

                var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
                    .Single(item => item.OwnerAssembly == typeof(ConcreteCodecDispatchEvidence).Assembly);
                var contract = manifest.Contracts.Single(item =>
                    item.ContractType == typeof(IConcreteCodecEvidenceRpc));
                var allFactories = manifest.Codecs.Concat(manifest.ContractCodecs).ToArray();

                return new EvidenceReport
                {
                    Ref = Environment.GetEnvironmentVariable("SHARPLINK_EVIDENCE_REF") ?? "unknown",
                    Runtime = RuntimeFeature.IsDynamicCodeSupported ? "jit" : "nativeaot",
                    TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
                    TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
                    RuntimeVersion = Environment.Version.ToString(),
                    WarmupOperations = warmupOperations,
                    RpcOperations = rpcOperations,
                    CodecOperations = codecOperations,
                    SampleCount = sampleCount,
                    NativeImageSizeBytes = RuntimeFeature.IsDynamicCodeSupported
                        ? null
                        : GetProcessImageSize(),
                    RpcAssemblyHash = manifest.RpcAssemblyHash,
                    ContractId = contract.ContractId,
                    ContractFingerprint = contract.Fingerprint,
                    Methods = contract.Methods
                        .OrderBy(static item => item.Name, StringComparer.Ordinal)
                        .Select(static item => new MethodIdentity(
                            item.Name,
                            item.MethodId,
                            item.Fingerprint))
                        .ToArray(),
                    SmallCodecHash = FindCodecHash(allFactories, typeof(ConcreteCodecSmallPayload)),
                    NestedCodecHash = FindCodecHash(allFactories, typeof(ConcreteCodecNestedPayload)),
                    SmallWireSha256 = Hash(smallBytes),
                    NestedWireSha256 = Hash(nestedBytes),
                    Cases = cases
                };
            }
            finally
            {
                runtimeContext.Buffers.Return(writer);
            }
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            await client.StopAsync().ConfigureAwait(false);
            await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private static byte[] SerializeOnce<T>(
        IRpcCodec<T> codec,
        T value,
        IRpcBufferWriterPool buffers)
    {
        var writer = buffers.Rent();
        try
        {
            codec.Serialize(value, writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            buffers.Return(writer);
        }
    }

    private static async Task<EvidenceCase> MeasureAsyncCase(
        string name,
        int warmupOperations,
        int operations,
        int sampleCount,
        Func<ValueTask> operation)
    {
        for (var index = 0; index < warmupOperations; index++)
            await operation().ConfigureAwait(false);

        var samples = new List<EvidenceSample>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            ForceGc();
            samples.Add(await MeasureAsyncSample(operations, operation).ConfigureAwait(false));
        }
        return BuildCase(name, operations, samples);
    }

    private static async Task<EvidenceCase> MeasureOneWayAsync(
        IConcreteCodecEvidenceRpc rpc,
        ConcreteCodecSmallPayload payload,
        int warmupOperations,
        int operations,
        int sampleCount)
    {
        var warmupTarget = ConcreteCodecEvidenceService.PublishedCount + warmupOperations;
        for (var index = 0; index < warmupOperations; index++)
            await rpc.PublishSmallAsync(payload).ConfigureAwait(false);
        await WaitUntilPublishedAsync(warmupTarget).ConfigureAwait(false);

        var samples = new List<EvidenceSample>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            ForceGc();
            var target = ConcreteCodecEvidenceService.PublishedCount + operations;
            samples.Add(await MeasureAsyncSample(
                operations,
                () => rpc.PublishSmallAsync(payload)).ConfigureAwait(false));
            await WaitUntilPublishedAsync(target).ConfigureAwait(false);
        }
        return BuildCase("small-generated-dto-oneway", operations, samples);
    }

    private static EvidenceCase MeasureSyncCase(
        string name,
        int warmupOperations,
        int operations,
        int sampleCount,
        Action operation)
    {
        for (var index = 0; index < warmupOperations; index++)
            operation();

        var samples = new List<EvidenceSample>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            ForceGc();
            samples.Add(MeasureSyncSample(operations, operation));
        }
        return BuildCase(name, operations, samples);
    }

    private static async Task<EvidenceSample> MeasureAsyncSample(
        int operations,
        Func<ValueTask> operation)
    {
        using var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
            await operation().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new EvidenceSample(
            elapsed.TotalNanoseconds / operations,
            cpu.TotalNanoseconds / operations,
            (double)allocated / operations);
    }

    private static EvidenceSample MeasureSyncSample(int operations, Action operation)
    {
        using var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
            operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new EvidenceSample(
            elapsed.TotalNanoseconds / operations,
            cpu.TotalNanoseconds / operations,
            (double)allocated / operations);
    }

    private static EvidenceCase BuildCase(
        string name,
        int operations,
        IReadOnlyList<EvidenceSample> samples)
        => new(
            name,
            operations,
            Median(samples.Select(static item => item.NanosecondsPerOperation)),
            Median(samples.Select(static item => item.CpuNanosecondsPerOperation)),
            Median(samples.Select(static item => item.AllocatedBytesPerOperation)),
            samples);

    private static async Task WaitUntilPublishedAsync(long target)
    {
        var timeout = Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency;
        while (ConcreteCodecEvidenceService.PublishedCount < target)
        {
            if (Stopwatch.GetTimestamp() >= timeout)
                throw new TimeoutException($"one-way evidence did not publish operation {target}");
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static RpcHash128 FindCodecHash(
        IEnumerable<IRpcGeneratedCodecFactory> factories,
        Type targetType)
    {
        var matches = factories.Where(item => item.TargetType == targetType).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"no generated Codec identity found for {targetType}");
        var hash = matches[0].CodecHash;
        if (matches.Any(item => item.CodecHash != hash))
            throw new InvalidOperationException($"conflicting generated Codec identities found for {targetType}");
        return hash;
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static long? GetProcessImageSize()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) || !File.Exists(path)
            ? null
            : new FileInfo(path).Length;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException("at least one evidence sample is required");
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentOutOfRangeException(name, value, "value must be a positive integer");
        return parsed;
    }

    private static void WriteReport(string outputPath, EvidenceReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("ref", report.Ref);
        json.WriteString("runtime", report.Runtime);
        json.WriteString("runtimeVersion", report.RuntimeVersion);
        json.WriteString("tieredCompilation", report.TieredCompilation);
        json.WriteString("tieredPgo", report.TieredPgo);
        json.WriteNumber("warmupOperations", report.WarmupOperations);
        json.WriteNumber("rpcOperations", report.RpcOperations);
        json.WriteNumber("codecOperations", report.CodecOperations);
        json.WriteNumber("sampleCount", report.SampleCount);
        if (report.NativeImageSizeBytes is { } nativeImageSizeBytes)
            json.WriteNumber("nativeImageSizeBytes", nativeImageSizeBytes);
        else
            json.WriteNull("nativeImageSizeBytes");

        json.WriteStartObject("identity");
        WriteHash(json, "rpcAssemblyHash", report.RpcAssemblyHash);
        json.WriteNumber("contractId", report.ContractId);
        json.WriteString("contractFingerprint", report.ContractFingerprint);
        WriteHash(json, "smallCodecHash", report.SmallCodecHash);
        WriteHash(json, "nestedCodecHash", report.NestedCodecHash);
        json.WriteString("smallWireSha256", report.SmallWireSha256);
        json.WriteString("nestedWireSha256", report.NestedWireSha256);
        json.WriteStartArray("methods");
        foreach (var method in report.Methods)
        {
            json.WriteStartObject();
            json.WriteString("name", method.Name);
            json.WriteNumber("methodId", method.MethodId);
            json.WriteString("fingerprint", method.Fingerprint);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        json.WriteStartArray("cases");
        foreach (var item in report.Cases)
        {
            json.WriteStartObject();
            json.WriteString("name", item.Name);
            json.WriteNumber("operationsPerSample", item.OperationsPerSample);
            json.WriteNumber("medianNanosecondsPerOperation", item.MedianNanosecondsPerOperation);
            json.WriteNumber("medianCpuNanosecondsPerOperation", item.MedianCpuNanosecondsPerOperation);
            json.WriteNumber("medianAllocatedBytesPerOperation", item.MedianAllocatedBytesPerOperation);
            json.WriteStartArray("samples");
            foreach (var sample in item.Samples)
            {
                json.WriteStartObject();
                json.WriteNumber("nanosecondsPerOperation", sample.NanosecondsPerOperation);
                json.WriteNumber("cpuNanosecondsPerOperation", sample.CpuNanosecondsPerOperation);
                json.WriteNumber("allocatedBytesPerOperation", sample.AllocatedBytesPerOperation);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteNumber("sink", Volatile.Read(ref s_sink));
        json.WriteEndObject();
        json.Flush();
    }

    private static void WriteHash(Utf8JsonWriter json, string name, RpcHash128 hash)
    {
        json.WriteStartObject(name);
        json.WriteNumber("high", hash.High);
        json.WriteNumber("low", hash.Low);
        json.WriteEndObject();
    }

    private sealed class EvidenceReport
    {
        public string Ref { get; init; } = string.Empty;
        public string Runtime { get; init; } = string.Empty;
        public string RuntimeVersion { get; init; } = string.Empty;
        public string TieredCompilation { get; init; } = string.Empty;
        public string TieredPgo { get; init; } = string.Empty;
        public int WarmupOperations { get; init; }
        public int RpcOperations { get; init; }
        public int CodecOperations { get; init; }
        public int SampleCount { get; init; }
        public long? NativeImageSizeBytes { get; init; }
        public RpcHash128 RpcAssemblyHash { get; init; }
        public long ContractId { get; init; }
        public string ContractFingerprint { get; init; } = string.Empty;
        public IReadOnlyList<MethodIdentity> Methods { get; init; } = [];
        public RpcHash128 SmallCodecHash { get; init; }
        public RpcHash128 NestedCodecHash { get; init; }
        public string SmallWireSha256 { get; init; } = string.Empty;
        public string NestedWireSha256 { get; init; } = string.Empty;
        public IReadOnlyList<EvidenceCase> Cases { get; init; } = [];
    }

    private sealed record MethodIdentity(string Name, long MethodId, string Fingerprint);

    private sealed record EvidenceCase(
        string Name,
        int OperationsPerSample,
        double MedianNanosecondsPerOperation,
        double MedianCpuNanosecondsPerOperation,
        double MedianAllocatedBytesPerOperation,
        IReadOnlyList<EvidenceSample> Samples);

    private sealed record EvidenceSample(
        double NanosecondsPerOperation,
        double CpuNanosecondsPerOperation,
        double AllocatedBytesPerOperation);
}

[RpcSerializable]
public sealed class ConcreteCodecSmallPayload
{
    [RpcMember(1)]
    public int Value { get; set; }

    [RpcMember(2)]
    public long Timestamp { get; set; }
}

[RpcSerializable]
public sealed class ConcreteCodecNestedPayload
{
    [RpcMember(1)]
    public ConcreteCodecSmallPayload Primary { get; set; } = new();

    [RpcMember(2)]
    public List<ConcreteCodecSmallPayload> Items { get; set; } = [];
}

[RpcContract]
public interface IConcreteCodecEvidenceRpc : IService
{
    [NonCancellable]
    ValueTask<ConcreteCodecSmallPayload> EchoSmallAsync(ConcreteCodecSmallPayload value);

    [NonCancellable]
    ValueTask<ConcreteCodecNestedPayload> EchoNestedAsync(ConcreteCodecNestedPayload value);

    [Oneway]
    [NonCancellable]
    ValueTask PublishSmallAsync(ConcreteCodecSmallPayload value);
}

[RpcService]
public sealed class ConcreteCodecEvidenceService : IConcreteCodecEvidenceRpc
{
    private static long s_publishedCount;

    internal static long PublishedCount => Volatile.Read(ref s_publishedCount);

    public ValueTask<ConcreteCodecSmallPayload> EchoSmallAsync(ConcreteCodecSmallPayload value)
        => ValueTask.FromResult(value);

    public ValueTask<ConcreteCodecNestedPayload> EchoNestedAsync(ConcreteCodecNestedPayload value)
        => ValueTask.FromResult(value);

    public ValueTask PublishSmallAsync(ConcreteCodecSmallPayload value)
    {
        _ = value;
        Interlocked.Increment(ref s_publishedCount);
        return ValueTask.CompletedTask;
    }
}
