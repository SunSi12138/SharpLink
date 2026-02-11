using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.LoadTest;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = LoadTestOptions.Parse(args);
        var metrics = new MetricsRegistry();
        using var metricsServer = options.MetricsPort > 0 ? new MetricsServer(options.MetricsPort, metrics) : null;

        switch (options.Mode)
        {
            case RunMode.Server:
                await RunServerOnlyAsync(options);
                return;
            case RunMode.Client:
                await RunClientOnlyAsync(options, metrics);
                return;
            case RunMode.Local:
            default:
                await RunLocalAsync(options, metrics);
                return;
        }
    }

    private static async Task RunLocalAsync(LoadTestOptions options, MetricsRegistry metrics)
    {
        var serverCts = new CancellationTokenSource();
        var serverToken = serverCts.Token;
        var server = CreateServer(options);
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
            await Task.Delay(200, serverToken);
            await RunClientOnlyAsync(options, metrics);
        }
        finally
        {
            await serverCts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task RunServerOnlyAsync(LoadTestOptions options)
    {
        var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            var server = CreateServer(options);
            Console.WriteLine($"[Server] listening on {options.BindIp}:{options.Port}");
            await server.Start(cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task RunClientOnlyAsync(LoadTestOptions options, MetricsRegistry metrics)
    {
        var client = CreateClient(options);
        try
        {
            var connected = await client.ConnectAsync();
            if (!connected)
                throw new InvalidOperationException("Load test client failed to connect.");

            var rpc = client.Get<ILoadTestService>();
            if (options.WarmupSeconds > 0)
            {
                Console.WriteLine($"[Client] warmup {options.WarmupSeconds}s");
                await ExecuteStageAsync(rpc, options.Operation, options.PayloadSize, options.WarmupSeconds, options.ConcurrencyConfig[0], metrics, isWarmup: true);
            }

            foreach (var concurrency in options.ConcurrencyConfig)
            {
                var result = await ExecuteStageAsync(rpc, options.Operation, options.PayloadSize, options.DurationSeconds, concurrency, metrics, isWarmup: false);
                Console.WriteLine(
                    $"[Result] op={result.Operation} c={result.Concurrency} qps={result.Qps:F2} " +
                    $"ok={result.Success} fail={result.Failure} p50={result.P50Us:F2}us p95={result.P95Us:F2}us p99={result.P99Us:F2}us");
            }
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    private static async Task<StageResult> ExecuteStageAsync(
        ILoadTestService rpc,
        string operation,
        int payloadSize,
        int durationSeconds,
        int concurrency,
        MetricsRegistry metrics,
        bool isWarmup)
    {
        var histogram = new LatencyHistogram();
        var realtimeHistogram = new LatencyHistogram();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var token = cts.Token;
        long success = 0;
        long failure = 0;
        long realtimeSuccess = 0;
        long realtimeFailure = 0;
        var workers = new Task[concurrency];
        var stageTimer = Stopwatch.StartNew();
        var lastRealtimeUpdate = stageTimer.Elapsed;

        Task? realtimeReporter = null;
        if (!isWarmup)
        {
            realtimeReporter = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    var now = stageTimer.Elapsed;
                    var windowSeconds = Math.Max(0.001, (now - lastRealtimeUpdate).TotalSeconds);
                    lastRealtimeUpdate = now;

                    var windowSuccess = Interlocked.Exchange(ref realtimeSuccess, 0);
                    var windowHistogram = Interlocked.Exchange(ref realtimeHistogram, new LatencyHistogram());

                    metrics.UpdateRealtime(new RealtimeResult(
                        operation,
                        concurrency,
                        windowSuccess / windowSeconds,
                        windowHistogram.Percentile(50),
                        windowHistogram.Percentile(95),
                        windowHistogram.Percentile(99)));
                }
            }, CancellationToken.None);
        }

        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        if (operation == "echo")
                        {
                            var payload = new string('x', payloadSize);
                            _ = await rpc.EchoAsync(payload);
                        }
                        else
                        {
                            _ = await rpc.AddAsync(7, 9);
                        }

                        var elapsedUs = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
                        histogram.Record(elapsedUs);
                        Volatile.Read(ref realtimeHistogram).Record(elapsedUs);
                        Interlocked.Increment(ref success);
                        Interlocked.Increment(ref realtimeSuccess);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failure);
                        Interlocked.Increment(ref realtimeFailure);
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers);
        if (realtimeReporter is not null)
            await realtimeReporter;

        var seconds = durationSeconds;
        var qps = success / Math.Max(1.0, seconds);
        var result = new StageResult(
            operation,
            concurrency,
            success,
            failure,
            qps,
            histogram.Percentile(50),
            histogram.Percentile(95),
            histogram.Percentile(99));

        if (!isWarmup)
            metrics.UpdateStage(result);

        return result;
    }

    private static ISharpLinkServer CreateServer(LoadTestOptions options)
    {
        return SharpLinkServerBuilder.Create()
            .AddService<ILoadTestService, LoadTestService>()
            .UseTcp(options.Port, options.BindIp)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(options.HeartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
            .Build();
    }

    private static ISharpLinkClient CreateClient(LoadTestOptions options)
    {
        return SharpClientBuilder.Create()
            .UseTcp(options.Host, options.Port)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds), TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
            .Build();
    }
}

public enum RunMode
{
    Local,
    Server,
    Client
}

public sealed class LoadTestOptions
{
    public RunMode Mode { get; private init; } = RunMode.Local;
    public string Host { get; private init; } = "127.0.0.1";
    public string BindIp { get; private init; } = "0.0.0.0";
    public int Port { get; private init; } = 19100;
    public int DurationSeconds { get; private init; } = 20;
    public int WarmupSeconds { get; private init; } = 5;
    public int[] ConcurrencyConfig { get; private init; } = [1, 2, 4, 8, 16, 32];
    public string Operation { get; private init; } = "add";
    public int PayloadSize { get; private init; } = 64;
    public int MetricsPort { get; private init; } = 9464;
    public int HeartbeatIntervalSeconds { get; private init; } = 10;
    public int HeartbeatCheckIntervalSeconds { get; private init; } = 10;
    public int HeartbeatTimeoutSeconds { get; private init; } = 120;

    public static LoadTestOptions Parse(string[] args)
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

        var mode = map.TryGetValue("mode", out var modeStr) && Enum.TryParse<RunMode>(modeStr, true, out var parsedMode)
            ? parsedMode
            : RunMode.Local;

        var concurrencyNum = map.TryGetValue("concurrency", out var concurrencyStr)
            ? concurrencyStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToArray()
            : [1, 2, 4, 8, 16, 32];

        return new LoadTestOptions
        {
            Mode = mode,
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19100")),
            DurationSeconds = int.Parse(map.GetValueOrDefault("duration", "20")),
            WarmupSeconds = int.Parse(map.GetValueOrDefault("warmup", "5")),
            ConcurrencyConfig = concurrencyNum.Length == 0 ? [1] : concurrencyNum,
            Operation = map.GetValueOrDefault("operation", "add").ToLowerInvariant(),
            PayloadSize = int.Parse(map.GetValueOrDefault("payload-size", "64")),
            MetricsPort = int.Parse(map.GetValueOrDefault("metrics-port", "9464")),
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
    double P99Us);

public sealed record RealtimeResult(
    string Operation,
    int Concurrency,
    double Qps,
    double P50Us,
    double P95Us,
    double P99Us);

internal sealed class LatencyHistogram
{
    private const int BucketCount = 3000;
    private readonly long[] _buckets = new long[BucketCount];
    private long _count;

    public void Record(double microseconds)
    {
        var bucket = (int)Math.Clamp(microseconds, 0, BucketCount - 1);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
    }

    public double Percentile(double p)
    {
        var count = Interlocked.Read(ref _count);
        if (count <= 0)
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
}

internal sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<int, StageResult> _stageByConcurrency = new();
    private readonly ConcurrentDictionary<int, RealtimeResult> _realtimeByConcurrency = new();
    private long _totalSuccess;
    private long _totalFailure;

    public void UpdateStage(StageResult result)
    {
        _stageByConcurrency[result.Concurrency] = result;
        Interlocked.Add(ref _totalSuccess, result.Success);
        Interlocked.Add(ref _totalFailure, result.Failure);
    }

    public void UpdateRealtime(RealtimeResult result)
    {
        _realtimeByConcurrency[result.Concurrency] = result;
    }

    public string RenderPrometheus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TYPE sharplink_load_test_total_success counter");
        sb.AppendLine($"sharplink_load_test_total_success {Interlocked.Read(ref _totalSuccess)}");
        sb.AppendLine("# TYPE sharplink_load_test_total_failure counter");
        sb.AppendLine($"sharplink_load_test_total_failure {Interlocked.Read(ref _totalFailure)}");
        sb.AppendLine("# TYPE sharplink_load_test_stage_qps gauge");
        sb.AppendLine("# TYPE sharplink_load_test_stage_latency_us gauge");
        foreach (var (c, r) in _stageByConcurrency.OrderBy(x => x.Key))
        {
            sb.AppendLine($"sharplink_load_test_stage_qps{{concurrency=\"{c}\",operation=\"{r.Operation}\"}} {r.Qps:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.50\"}} {r.P50Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.95\"}} {r.P95Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.99\"}} {r.P99Us:F2}");
        }

        sb.AppendLine("# TYPE sharplink_load_test_realtime_qps gauge");
        sb.AppendLine("# TYPE sharplink_load_test_realtime_latency_us gauge");
        foreach (var (c, r) in _realtimeByConcurrency.OrderBy(x => x.Key))
        {
            sb.AppendLine($"sharplink_load_test_realtime_qps{{concurrency=\"{c}\",operation=\"{r.Operation}\"}} {r.Qps:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.50\"}} {r.P50Us:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.95\"}} {r.P95Us:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{c}\",operation=\"{r.Operation}\",quantile=\"0.99\"}} {r.P99Us:F2}");
        }

        return sb.ToString();
    }
}

internal sealed class MetricsServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly HttpListener _listener;
    private readonly MetricsRegistry _registry;

    public MetricsServer(int port, MetricsRegistry registry)
    {
        _registry = registry;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/metrics/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
        Console.WriteLine($"[Metrics] http://localhost:{port}/metrics");
    }

    private async Task LoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var body = _registry.RenderPrometheus();
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/plain; version=0.0.4";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }
}

public interface ILoadTestService : IService
{
    ValueTask<int> AddAsync(int left, int right);
    ValueTask<string> EchoAsync(string value);
}

[RpcService]
public class LoadTestService : ILoadTestService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);
}
