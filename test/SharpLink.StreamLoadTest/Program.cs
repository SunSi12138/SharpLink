using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

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
            case RunMode.Local:
            default:
                await RunLocalAsync(options);
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.StreamLoadTest options:");
        Console.WriteLine("  --mode local|server|client");
        Console.WriteLine("  --host 127.0.0.1 --bind-ip 0.0.0.0 --port 19150");
        Console.WriteLine("  --duration 20 --warmup 5 --concurrency 1,2,4,8,16");
        Console.WriteLine("  --operation all|unary|c2s|s2c|duplex");
        Console.WriteLine("  --stream-size 256");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --operation all --concurrency 1,4,16 --stream-size 512");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode server --port 19150");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode client --host 127.0.0.1 --port 19150 --operation duplex");
    }

    private static void PrintConfig(StreamLoadOptions options)
    {
        Console.WriteLine($"[Config] mode={options.Mode} op={options.Operation} duration={options.DurationSeconds}s warmup={options.WarmupSeconds}s streamSize={options.StreamSize}");
        Console.WriteLine($"[Config] tcp://{options.Host}:{options.Port} (bind={options.BindIp}) concurrency=[{string.Join(',', options.ConcurrencyConfig)}]");
    }

    private static async Task RunLocalAsync(StreamLoadOptions options)
    {
        var server = CreateServer(options);
        var client = CreateClient(options);
        using var cts = new CancellationTokenSource();
        var serverToken = cts.Token;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(serverToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            await Task.Delay(200, CancellationToken.None);
            await RunClientStagesAsync(options, client);
        }
        finally
        {
            await cts.CancelAsync();
            (client as IDisposable)?.Dispose();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task RunServerOnlyAsync(StreamLoadOptions options)
    {
        using var cancel = new ConsoleCancelScope();
        var server = CreateServer(options);
        Console.WriteLine("[Server] started");
        await server.Start(cancel.Token);
    }

    private static async Task RunClientOnlyAsync(StreamLoadOptions options)
    {
        var client = CreateClient(options);
        try
        {
            await RunClientStagesAsync(options, client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    private static async Task RunClientStagesAsync(StreamLoadOptions options, ISharpLinkClient client)
    {
        var connected = await client.ConnectAsync();
        if (!connected)
            throw new InvalidOperationException("client connect failed");

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

    private static async IAsyncEnumerable<int> ToStream(int[] values, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
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

    private static ISharpLinkServer CreateServer(StreamLoadOptions options)
        => SharpLinkServerBuilder.Create()
            .AddService<IStreamLoadService, StreamLoadService>()
            .UseTcp(options.Port, options.BindIp)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120))
            .Build();

    private static ISharpLinkClient CreateClient(StreamLoadOptions options)
        => SharpClientBuilder.Create()
            .UseTcp(options.Host, options.Port)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120))
            .Build();
}

public sealed class StreamLoadOptions
{
    public RunMode Mode { get; private init; } = RunMode.Local;
    public string Host { get; private init; } = "127.0.0.1";
    public string BindIp { get; private init; } = "0.0.0.0";
    public int Port { get; private init; } = 19150;
    public int DurationSeconds { get; private init; } = 20;
    public int WarmupSeconds { get; private init; } = 5;
    public int[] ConcurrencyConfig { get; private init; } = [1, 2, 4, 8, 16];
    public string Operation { get; private init; } = "all";
    public int StreamSize { get; private init; } = 256;

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
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19150")),
            DurationSeconds = int.Parse(map.GetValueOrDefault("duration", "20")),
            WarmupSeconds = int.Parse(map.GetValueOrDefault("warmup", "5")),
            ConcurrencyConfig = concurrencyConfig.Length == 0 ? [1] : concurrencyConfig,
            Operation = operation,
            StreamSize = int.Parse(map.GetValueOrDefault("stream-size", "256"))
        };
    }
}

public enum RunMode
{
    Local,
    Server,
    Client
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

internal sealed class LatencyHistogram
{
    private const int BucketCount = 2_000_000;
    private readonly long[] _buckets = new long[BucketCount];
    private long _count;
    private long _sum;
    private long _max;

    public void Record(double microseconds)
    {
        var us = (long)Math.Max(0, Math.Round(microseconds));
        var bucket = (int)Math.Clamp(us, 0, _buckets.Length - 1);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sum, us);
        UpdateMax(us);
    }

    public double Percentile(double p)
    {
        var count = Interlocked.Read(ref _count);
        if (count == 0)
            return 0;

        var target = (long)Math.Ceiling(count * (p / 100.0));
        long running = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            running += Interlocked.Read(ref _buckets[i]);
            if (running >= target)
                return i;
        }

        return _buckets.Length - 1;
    }

    public double Average
    {
        get
        {
            var count = Interlocked.Read(ref _count);
            return count == 0 ? 0 : Interlocked.Read(ref _sum) / (double)count;
        }
    }

    public double Max => Interlocked.Read(ref _max);

    private void UpdateMax(long value)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _max);
            if (value <= old)
                return;
            if (Interlocked.CompareExchange(ref _max, value, old) == old)
                return;
        }
    }
}

internal sealed class FailureRecorder
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Record(Exception ex)
    {
        var key = ex.GetType().Name;
        _counts.AddOrUpdate(key, 1, static (_, old) => old + 1);
    }

    public string Top(int count)
    {
        return _counts.IsEmpty ? string.Empty : string.Join(", ", _counts.OrderByDescending(kv => kv.Value).Take(count).Select(kv => $"{kv.Key}:{kv.Value}"));
    }
}

internal sealed class ConsoleCancelScope : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConsoleCancelEventHandler _handler;
    private bool _disposed;

    public ConsoleCancelScope()
    {
        _handler = OnCancel;
        Console.CancelKeyPress += _handler;
    }

    public CancellationToken Token => _cts.Token;

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Console.CancelKeyPress -= _handler;
        _cts.Dispose();
    }
}

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
