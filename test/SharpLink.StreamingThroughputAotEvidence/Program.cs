using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.StreamingThroughputAotEvidence;

[RpcContract]
public interface IPhase3StreamingRpc : IService
{
    [NonCancellable]
    ValueTask<long> UploadPayloadsAsync(IAsyncEnumerable<byte[]> payloads);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DownloadPayloadsAsync(int count, int payloadSize);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DuplexPayloadsAsync(IAsyncEnumerable<byte[]> payloads);
}

[RpcService]
public sealed class Phase3StreamingRpcService : IPhase3StreamingRpc
{
    private static readonly byte[] SPayload16 = CreatePayload(16, 17, 31);
    private static readonly byte[] SPayload4096 = CreatePayload(4096, 23, 47);

    public async ValueTask<long> UploadPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        long score = 0;
        await foreach (var payload in payloads)
            score += GetPayloadScore(payload);
        return score;
    }

    public async IAsyncEnumerable<byte[]> DownloadPayloadsAsync(int count, int payloadSize)
    {
        var payload = GetPayload(payloadSize);
        for (var i = 0; i < count; i++)
        {
            yield return payload;
            await Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<byte[]> DuplexPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        await foreach (var payload in payloads)
            yield return payload;
    }

    internal static byte[] GetPayload(int payloadSize) => payloadSize switch
    {
        16 => SPayload16,
        4096 => SPayload4096,
        _ => throw new ArgumentOutOfRangeException(nameof(payloadSize))
    };

    internal static long GetPayloadScore(byte[] payload)
        => payload.Length == 0 ? 0 : payload.Length + payload[0] + payload[^1];

    private static byte[] CreatePayload(int length, byte first, byte last)
    {
        var payload = new byte[length];
        payload[0] = first;
        payload[^1] = last;
        return payload;
    }
}

internal sealed class Phase3Environment : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _serverTask;
    private readonly ISharpLinkServer _server;
    private readonly ISharpLinkClient _client;

    private Phase3Environment(
        IPhase3StreamingRpc rpc,
        CancellationTokenSource shutdown,
        Task serverTask,
        ISharpLinkServer server,
        ISharpLinkClient client)
    {
        Rpc = rpc;
        _shutdown = shutdown;
        _serverTask = serverTask;
        _server = server;
        _client = client;
    }

    internal IPhase3StreamingRpc Rpc { get; }

    internal static async Task<Phase3Environment> CreateAsync()
    {
        var service = new Phase3StreamingRpcService();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        serverBuilder.ReplaceService<IPhase3StreamingRpc>(service);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunUntilStoppedAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        var clientBuilder = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port);
        clientBuilder.DisableRequestTimeout();
        var client = clientBuilder.Build();

        try
        {
            await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            return new Phase3Environment(
                client.Get<IPhase3StreamingRpc>(),
                shutdown,
                serverTask,
                server,
                client);
        }
        catch
        {
            shutdown.Cancel();
            await client.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            shutdown.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _client.StopAsync().ConfigureAwait(false);
        await _server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        await Task.WhenAny(_serverTask, Task.Delay(500)).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    internal static async IAsyncEnumerable<T> ToStream<T>(
        IReadOnlyList<T> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.CompletedTask;
        }
    }
}

internal enum Phase3Scenario
{
    Server1x16,
    Server100x16,
    Server100x4096,
    Client100x16,
    Client100x4096,
    Duplex100x16,
    Duplex100x4096
}

internal sealed class Phase3Case : IAsyncDisposable
{
    private readonly Phase3Environment _environment;

    private Phase3Case(
        Phase3Environment environment,
        string shape,
        int itemCount,
        int itemBytes,
        Func<ValueTask<long>> invoke)
    {
        _environment = environment;
        Shape = shape;
        ItemCount = itemCount;
        ItemBytes = itemBytes;
        InvokeAsync = invoke;
        Expected = itemCount * Phase3StreamingRpcService.GetPayloadScore(
            Phase3StreamingRpcService.GetPayload(itemBytes));
    }

    internal string Shape { get; }
    internal int ItemCount { get; }
    internal int ItemBytes { get; }
    internal long Expected { get; }
    internal Func<ValueTask<long>> InvokeAsync { get; }

    internal static async Task<Phase3Case> CreateAsync(Phase3Scenario scenario)
    {
        var environment = await Phase3Environment.CreateAsync().ConfigureAwait(false);
        try
        {
            var (shape, itemCount, itemBytes) = scenario switch
            {
                Phase3Scenario.Server1x16 => ("ServerStreaming", 1, 16),
                Phase3Scenario.Server100x16 => ("ServerStreaming", 100, 16),
                Phase3Scenario.Server100x4096 => ("ServerStreaming", 100, 4096),
                Phase3Scenario.Client100x16 => ("ClientStreaming", 100, 16),
                Phase3Scenario.Client100x4096 => ("ClientStreaming", 100, 4096),
                Phase3Scenario.Duplex100x16 => ("Duplex", 100, 16),
                Phase3Scenario.Duplex100x4096 => ("Duplex", 100, 4096),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };

            var payload = Phase3StreamingRpcService.GetPayload(itemBytes);
            var payloads = Enumerable.Repeat(payload, itemCount).ToArray();
            Func<ValueTask<long>> invoke = shape switch
            {
                "ServerStreaming" => () => InvokeServerAsync(
                    environment.Rpc, itemCount, itemBytes),
                "ClientStreaming" => async () => await environment.Rpc
                    .UploadPayloadsAsync(Phase3Environment.ToStream(payloads))
                    .ConfigureAwait(false),
                "Duplex" => () => InvokeDuplexAsync(environment.Rpc, payloads),
                _ => throw new InvalidOperationException()
            };

            return new Phase3Case(
                environment, shape, itemCount, itemBytes, invoke);
        }
        catch
        {
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _environment.DisposeAsync();

    private static async ValueTask<long> InvokeServerAsync(
        IPhase3StreamingRpc rpc,
        int count,
        int bytes)
    {
        long score = 0;
        await foreach (var payload in rpc.DownloadPayloadsAsync(count, bytes))
            score += Phase3StreamingRpcService.GetPayloadScore(payload);
        return score;
    }

    private static async ValueTask<long> InvokeDuplexAsync(
        IPhase3StreamingRpc rpc,
        IReadOnlyList<byte[]> payloads)
    {
        long score = 0;
        await foreach (var payload in rpc.DuplexPayloadsAsync(
                           Phase3Environment.ToStream(payloads)))
            score += Phase3StreamingRpcService.GetPayloadScore(payload);
        return score;
    }
}

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 5)
        {
            throw new ArgumentException(
                "Usage: <scenario> <warmup-operations> <measurement-seconds> " +
                "<max-operations> <output-json>");
        }

        var scenario = Enum.Parse<Phase3Scenario>(args[0], ignoreCase: true);
        var warmup = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var output = Path.GetFullPath(args[4]);

        await using var benchmark = await Phase3Case.CreateAsync(scenario).ConfigureAwait(false);

        var firstStarted = Stopwatch.GetTimestamp();
        Validate(await benchmark.InvokeAsync().ConfigureAwait(false), benchmark.Expected);
        var firstCallUs = Stopwatch.GetElapsedTime(firstStarted).TotalMicroseconds;

        for (var i = 0; i < warmup; i++)
            Validate(await benchmark.InvokeAsync().ConfigureAwait(false), benchmark.Expected);

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
        var startedAt = Stopwatch.GetTimestamp();
        var deadline = startedAt + checked((long)Math.Ceiling(seconds * Stopwatch.Frequency));
        var completed = 0;
        long totalTicks = 0;

        while (completed < maxOperations && Stopwatch.GetTimestamp() < deadline)
        {
            var started = Stopwatch.GetTimestamp();
            Validate(await benchmark.InvokeAsync().ConfigureAwait(false), benchmark.Expected);
            var elapsed = Stopwatch.GetTimestamp() - started;
            latencies[completed++] = elapsed;
            totalTicks += elapsed;
        }

        var elapsedTotal = Stopwatch.GetElapsedTime(startedAt);
        process.Refresh();
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var cpuAfter = process.TotalProcessorTime;
        if (completed == 0)
            throw new InvalidOperationException("No operations completed.");

        Array.Sort(latencies, 0, completed);
        var allocated = allocatedAfter - allocatedBefore;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using var stream = File.Create(output);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("commit", Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown");
        writer.WriteString("scenario", scenario.ToString());
        writer.WriteString("shape", benchmark.Shape);
        writer.WriteNumber("itemCount", benchmark.ItemCount);
        writer.WriteNumber("itemBytes", benchmark.ItemBytes);
        writer.WriteString("timestampUtc", DateTimeOffset.UtcNow);
        writer.WriteString("hostName", Environment.MachineName);
        writer.WriteString("operatingSystem", RuntimeInformation.OSDescription);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteString("runtimeVersion", RuntimeInformation.FrameworkDescription);
        writer.WriteNumber("processorCount", Environment.ProcessorCount);
        writer.WriteBoolean("serverGc", GCSettings.IsServerGC);
        writer.WriteString("tieredCompilation", "NativeAOT");
        writer.WriteString("tieredPgo", "NativeAOT");
        writer.WriteNumber("warmupOperations", warmup);
        writer.WriteNumber("requestedMeasurementSeconds", seconds);
        writer.WriteNumber("actualMeasurementSeconds", elapsedTotal.TotalSeconds);
        writer.WriteNumber("operations", completed);
        writer.WriteNumber("throughputOperationsPerSecond", completed / elapsedTotal.TotalSeconds);
        writer.WriteNumber("throughputItemsPerSecond", completed * benchmark.ItemCount / elapsedTotal.TotalSeconds);
        writer.WriteNumber("firstCallUs", firstCallUs);
        writer.WriteNumber("averageUs", TicksToUs(totalTicks / (double)completed));
        writer.WriteNumber("p50Us", Percentile(latencies, completed, 50));
        writer.WriteNumber("p99Us", Percentile(latencies, completed, 99));
        writer.WriteNumber("p999Us", Percentile(latencies, completed, 99.9));
        writer.WriteNumber("maxUs", TicksToUs(latencies[completed - 1]));
        writer.WriteNumber("cpuUsPerOperation", (cpuAfter - cpuBefore).TotalMicroseconds / completed);
        writer.WriteNumber("allocatedBytesPerOperation", allocated / (double)completed);
        writer.WriteNumber("allocatedBytesPerItem", allocated / (double)(completed * benchmark.ItemCount));
        writer.WriteNumber("gen0Collections", GC.CollectionCount(0) - gen0Before);
        writer.WriteNumber("gen1Collections", GC.CollectionCount(1) - gen1Before);
        writer.WriteNumber("gen2Collections", GC.CollectionCount(2) - gen2Before);
        writer.WriteNumber("threadCount", process.Threads.Count);
        writer.WriteNumber("workingSetBytes", process.WorkingSet64);
        writer.WriteNumber("validationFailures", 0);
        writer.WriteBoolean("hitOperationLimit", completed == maxOperations);
        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static void Validate(long actual, long expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static double Percentile(long[] values, int count, double percentile)
    {
        var rank = Math.Clamp((int)Math.Ceiling(percentile / 100 * count) - 1, 0, count - 1);
        return TicksToUs(values[rank]);
    }

    private static double TicksToUs(double ticks)
        => ticks * 1_000_000d / Stopwatch.Frequency;
}
