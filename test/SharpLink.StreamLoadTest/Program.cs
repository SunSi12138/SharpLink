using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.LoadTestBase;
using SharpLink.Sdk;

namespace SharpLink.StreamLoadTest;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Any(static x => x is "--help" or "-h"))
        {
            PrintHelp();
            return;
        }

        var options = StreamLoadOptions.Parse(args);
        PrintConfig(options);

        switch (options.Mode)
        {
            case RunMode.Server:
                await RunServerOnlyAsync(options);
                break;
            case RunMode.Client:
                await RunClientOnlyAsync(options);
                break;
            default:
                await RunLocalAsync(options);
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.StreamLoadTest options:");
        Console.WriteLine("  --mode local|server|client");
        Console.WriteLine("  --transport tcp|uds|namedpipe|anonymous");
        Console.WriteLine("  --host 127.0.0.1 --bind-ip 0.0.0.0 --port 19150");
        Console.WriteLine("  --duration 20 --warmup 5 --concurrency 1,2,4,8,16");
        Console.WriteLine("  --operation all|unary|c2s|s2c|duplex");
        Console.WriteLine("  --stream-size 256");
        Console.WriteLine("  --heartbeat-interval 10 --heartbeat-check-interval 10 --heartbeat-timeout 120");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --transport tcp --operation all --concurrency 1,4,16 --stream-size 512");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --transport namedpipe --operation duplex");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode server --transport tcp --port 19150");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode client --transport tcp --host 127.0.0.1 --port 19150 --operation duplex");
    }

    private static void PrintConfig(StreamLoadOptions options)
    {
        Console.WriteLine($"[Config] mode={options.Mode} transport={options.Transport} op={options.Operation} duration={options.DurationSeconds}s warmup={options.WarmupSeconds}s streamSize={options.StreamSize}");
        Console.WriteLine($"[Config] concurrency=[{string.Join(',', options.ConcurrencyConfig)}]");

        if (options.Transport == TransportMode.Tcp)
            Console.WriteLine($"[Config] tcp://{options.Host}:{options.Port} (bind={options.BindIp})");
        else if (options.Transport == TransportMode.Uds)
            Console.WriteLine($"[Config] uds://{options.UdsPath}");
        else if (options.Transport == TransportMode.NamedPipe)
            Console.WriteLine($"[Config] pipe://{options.PipeName}");
        else
            Console.WriteLine("[Config] anonymous-pipe(local only)");
    }

    private static async Task RunLocalAsync(StreamLoadOptions options)
    {
        await using var harness = await LoadTestTransportFactory.CreateLocalHarness(
            options.Transport,
            options.Host,
            options.BindIp,
            options.Port,
            options.UdsPath,
            options.PipeName,
            options.HeartbeatIntervalSeconds,
            options.HeartbeatCheckIntervalSeconds,
            options.HeartbeatTimeoutSeconds,
            static builder => builder.AddService<IStreamLoadService, StreamLoadService>());

        using var serverCts = new CancellationTokenSource();
        var serverTask = RunServerLoopAsync(harness.Server, serverCts.Token);

        try
        {
            await Task.Delay(200, CancellationToken.None);
            await RunClientStagesAsync(options, harness.Client);
        }
        finally
        {
            await serverCts.CancelAsync();
            await harness.DisposeServerAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task RunServerLoopAsync(ISharpLinkServer server, CancellationToken token)
    {
        try
        {
            await server.RunAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunServerOnlyAsync(StreamLoadOptions options)
    {
        if (options.Transport == TransportMode.AnonymousPipe)
            throw new InvalidOperationException("Anonymous pipe mode only supports --mode local.");

        using var cancel = new ConsoleCancelScope();
        var server = LoadTestTransportFactory.CreateServer(
            options.Transport,
            options.BindIp,
            options.Port,
            options.UdsPath,
            options.PipeName,
            options.HeartbeatCheckIntervalSeconds,
            options.HeartbeatTimeoutSeconds,
            static builder => builder.AddService<IStreamLoadService, StreamLoadService>());

        Console.WriteLine("[Server] started");
        await server.RunAsync(cancel.Token);
    }

    private static async Task RunClientOnlyAsync(StreamLoadOptions options)
    {
        if (options.Transport == TransportMode.AnonymousPipe)
            throw new InvalidOperationException("Anonymous pipe mode only supports --mode local.");

        var client = LoadTestTransportFactory.CreateClient(
            options.Transport,
            options.Host,
            options.Port,
            options.UdsPath,
            options.PipeName,
            options.HeartbeatIntervalSeconds,
            options.HeartbeatTimeoutSeconds);

        try
        {
            await RunClientStagesAsync(options, client);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task RunClientStagesAsync(StreamLoadOptions options, ISharpLinkClient client)
    {
        await client.ConnectAsync();

        var rpc = client.Get<IStreamLoadService>();
        foreach (var operation in ResolveOperations(options.Operation))
        {
            foreach (var concurrency in options.ConcurrencyConfig)
            {
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"[Warmup] op={operation} c={concurrency} for {options.WarmupSeconds}s");
                    _ = await ExecuteStageAsync(rpc, operation, options.StreamSize, options.WarmupSeconds, concurrency);
                }

                var result = await ExecuteStageAsync(rpc, operation, options.StreamSize, options.DurationSeconds, concurrency);
                Console.WriteLine($"[Result] op={result.Operation} c={result.Concurrency} qps={result.Qps:F2} ok={result.Success} fail={result.Failure} err={result.ErrorRatePercent:F2}% p50={result.P50Us:F2}us p95={result.P95Us:F2}us p99={result.P99Us:F2}us avg={result.AvgUs:F2}us max={result.MaxUs:F2}us dur={result.ElapsedSeconds:F2}s");
                if (!string.IsNullOrEmpty(result.TopFailures))
                    Console.WriteLine($"[Failures] {result.TopFailures}");
            }
        }
    }

    private static async Task<StageResult> ExecuteStageAsync(
        IStreamLoadService rpc,
        string operation,
        int streamSize,
        int durationSeconds,
        int concurrency)
    {
        var histogram = new LatencyHistogram();
        var failures = new FailureRecorder();
        var payload = Enumerable.Range(1, streamSize).ToArray();

        long success = 0;
        long failure = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var token = cts.Token;
        var timer = Stopwatch.StartNew();
        var workers = new Task[concurrency];

        for (var i = 0; i < concurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        await InvokeOperationAsync(rpc, operation, payload, token);
                        var us = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
                        histogram.Record(us);
                        Interlocked.Increment(ref success);
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        failures.Record(ex);
                        Interlocked.Increment(ref failure);
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers);

        var elapsed = Math.Max(0.001, timer.Elapsed.TotalSeconds);
        var total = success + failure;
        var errRate = total == 0 ? 0 : failure * 100.0 / total;
        return new StageResult(
            operation,
            concurrency,
            success,
            failure,
            success / elapsed,
            histogram.Percentile(50),
            histogram.Percentile(95),
            histogram.Percentile(99),
            histogram.Average,
            histogram.Max,
            elapsed,
            errRate,
            failures.Top(3));
    }

    private static async Task InvokeOperationAsync(IStreamLoadService rpc, string operation, int[] payload, CancellationToken ct)
    {
        switch (operation)
        {
            case "unary":
                _ = await rpc.AddAsync(7, 9);
                return;
            case "c2s":
                _ = await rpc.UploadAsync(ToStream(payload, ct));
                return;
            case "s2c":
                await DrainAsync(rpc.DownloadAsync(payload.Length), ct);
                return;
            case "duplex":
                await DrainAsync(rpc.DuplexAsync(ToStream(payload, ct)), ct);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation");
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<int> stream, CancellationToken ct)
    {
        await foreach (var item in stream.WithCancellation(ct))
            _ = item;
    }

    private static async IAsyncEnumerable<int> ToStream(int[] values, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var t in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return t;
            await Task.CompletedTask;
        }
    }

    private static IReadOnlyList<string> ResolveOperations(string op)
        => op == "all" ? ["unary", "c2s", "s2c", "duplex"] : [op];
}

public sealed class StreamLoadOptions
{
    public RunMode Mode { get; private init; } = RunMode.Local;
    public TransportMode Transport { get; private init; } = TransportMode.Tcp;
    public string Host { get; private init; } = "127.0.0.1";
    public string BindIp { get; private init; } = "0.0.0.0";
    public int Port { get; private init; } = 19150;
    public string UdsPath { get; private init; } = TransportDefaults.GetDefaultUdsPath("sl_stream_loadtest");
    public string PipeName { get; private init; } = TransportDefaults.GetDefaultPipeName("sharplink-stream-loadtest");
    public int DurationSeconds { get; private init; } = 20;
    public int WarmupSeconds { get; private init; } = 5;
    public int[] ConcurrencyConfig { get; private init; } = [1, 2, 4, 8, 16];
    public string Operation { get; private init; } = "all";
    public int StreamSize { get; private init; } = 256;
    public int HeartbeatIntervalSeconds { get; private init; } = 10;
    public int HeartbeatCheckIntervalSeconds { get; private init; } = 10;
    public int HeartbeatTimeoutSeconds { get; private init; } = 120;

    public static StreamLoadOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            map[key] = value;
        }

        var mode = map.TryGetValue("mode", out var modeValue) && Enum.TryParse<RunMode>(modeValue, true, out var parsedMode)
            ? parsedMode
            : RunMode.Local;
        var transport = map.TryGetValue("transport", out var transportValue) && TransportDefaults.TryParseTransport(transportValue, out var parsedTransport)
            ? parsedTransport
            : TransportMode.Tcp;

        var operation = map.GetValueOrDefault("operation", "all").ToLowerInvariant();
        if (operation is not ("all" or "unary" or "c2s" or "s2c" or "duplex"))
            throw new ArgumentException($"Unsupported operation: {operation}. Supported: all, unary, c2s, s2c, duplex.");

        var concurrencyConfig = map.TryGetValue("concurrency", out var concurrencyStr)
            ? concurrencyStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToArray()
            : [1, 2, 4, 8, 16];

        return new StreamLoadOptions
        {
            Mode = mode,
            Transport = transport,
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19150")),
            UdsPath = map.GetValueOrDefault("uds-path", TransportDefaults.GetDefaultUdsPath("sl_stream_loadtest")),
            PipeName = map.GetValueOrDefault("pipe-name", TransportDefaults.GetDefaultPipeName("sharplink-stream-loadtest")),
            DurationSeconds = int.Parse(map.GetValueOrDefault("duration", "20")),
            WarmupSeconds = int.Parse(map.GetValueOrDefault("warmup", "5")),
            ConcurrencyConfig = concurrencyConfig.Length == 0 ? [1] : concurrencyConfig,
            Operation = operation,
            StreamSize = int.Parse(map.GetValueOrDefault("stream-size", "256")),
            HeartbeatIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-interval", "10")),
            HeartbeatCheckIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-check-interval", "10")),
            HeartbeatTimeoutSeconds = int.Parse(map.GetValueOrDefault("heartbeat-timeout", "120"))
        };
    }
}

public sealed record StageResult(
    string Operation,
    int Concurrency,
    long Success,
    long Failure,
    double Qps,
    double P50Us,
    double P95Us,
    double P99Us,
    double AvgUs,
    double MaxUs,
    double ElapsedSeconds,
    double ErrorRatePercent,
    string TopFailures);

[RpcContract]
public interface IStreamLoadService : IService
{
    ValueTask<int> AddAsync(int left, int right);
    ValueTask<long> UploadAsync(IAsyncEnumerable<int> values);
    IAsyncEnumerable<int> DownloadAsync(int count);
    IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values);
}

[RpcService]
public class StreamLoadService : IStreamLoadService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<long> UploadAsync(IAsyncEnumerable<int> values)
    {
        long sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> DownloadAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values)
    {
        await foreach (var value in values)
        {
            yield return value + 1;
            await Task.CompletedTask;
        }
    }
}
