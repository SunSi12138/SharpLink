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
using SharpLink.LoadTestBase;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.LoadTest;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Any(static x => string.Equals(x, "--help", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(x, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return;
        }

        var options = LoadTestOptions.Parse(args);
        var metrics = new MetricsRegistry();
        using var metricsServer = options.MetricsPort > 0 ? new MetricsServer(options.MetricsPort, metrics) : null;

        PrintConfig(options);

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

    private static void PrintConfig(LoadTestOptions options)
    {
        Console.WriteLine(
            $"[Config] mode={options.Mode} transport={options.Transport} operation={options.Operation} " +
            $"duration={options.DurationSeconds}s warmup={options.WarmupSeconds}s concurrency=[{string.Join(",", options.ConcurrencyConfig)}] " +
            $"payload={options.PayloadSize}B pool={options.MinConnections}/{options.MaxConnections} " +
            $"profile={options.PerformanceProfile} requestTimeout={options.RequestTimeoutMode}");

        if (options.Transport == TransportMode.Tcp)
            Console.WriteLine($"[Config] tcp://{options.Host}:{options.Port} (bind={options.BindIp})");
        else if (options.Transport == TransportMode.Uds)
            Console.WriteLine($"[Config] uds://{options.UdsPath}");
        else if (options.Transport == TransportMode.NamedPipe)
            Console.WriteLine($"[Config] pipe://{options.PipeName}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.LoadTest options:");
        Console.WriteLine("  --mode local|server|client");
        Console.WriteLine("  --transport tcp|uds|namedpipe|anonymous");
        Console.WriteLine("  --host 127.0.0.1 --bind-ip 0.0.0.0 --port 19100");
        Console.WriteLine("  --duration 20 --warmup 5 --concurrency 1,2,4,8,16,32");
        Console.WriteLine("  --operation add|echo|yield|delay --payload-size 64");
        Console.WriteLine("  --min-connections 1 --max-connections 1");
        Console.WriteLine("  --profile balanced|lowlatency|throughput");
        Console.WriteLine("  --request-timeout default|disabled|1ms|10ms|100ms");
        Console.WriteLine("  --json-output artifacts/perf/load.json");
        Console.WriteLine("  --metrics-port 9464");
        Console.WriteLine("  --heartbeat-interval 10 --heartbeat-check-interval 10 --heartbeat-timeout 120");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport tcp");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport namedpipe");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport uds");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport anonymous");
    }

    private static async Task RunLocalAsync(LoadTestOptions options, MetricsRegistry metrics)
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
            options.MinConnections,
            options.MaxConnections,
            static builder => builder.AddService<ILoadTestService, LoadTestService>(),
            options.PerformanceProfile,
            options.DisableRequestTimeout,
            options.RequestTimeout);
        var serverCts = new CancellationTokenSource();
        var serverToken = serverCts.Token;
        var serverTask = RunServerLoopAsync(harness.Server, serverToken);

        try
        {
            await Task.Delay(200, serverToken);
            await RunClientOnlyAsync(options, metrics, harness.Client);
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

    private static async Task RunServerOnlyAsync(LoadTestOptions options)
    {
        if (options.Transport == TransportMode.AnonymousPipe)
            throw new InvalidOperationException("Anonymous pipe mode only supports --mode local.");

        using var cancelScope = new ConsoleCancelScope();
        var server = LoadTestTransportFactory.CreateServer(
            options.Transport,
            options.BindIp,
            options.Port,
            options.UdsPath,
            options.PipeName,
            options.HeartbeatCheckIntervalSeconds,
            options.HeartbeatTimeoutSeconds,
            static builder => builder.AddService<ILoadTestService, LoadTestService>(),
            options.PerformanceProfile);
        Console.WriteLine("[Server] started.");
        await server.RunAsync(cancelScope.Token);
    }

    private static async Task RunClientOnlyAsync(LoadTestOptions options, MetricsRegistry metrics, ISharpLinkClient? clientOverride = null)
    {
        var ownedClient = clientOverride is null
            ? LoadTestTransportFactory.CreateClient(
                options.Transport,
                options.Host,
                options.Port,
                options.UdsPath,
                options.PipeName,
                options.HeartbeatIntervalSeconds,
                options.HeartbeatTimeoutSeconds,
                options.MinConnections,
                options.MaxConnections,
                options.PerformanceProfile,
                options.DisableRequestTimeout,
                options.RequestTimeout)
            : null;
        var client = clientOverride ?? ownedClient!;
        var results = new List<StageResult>();
        try
        {
            await client.ConnectAsync();

            var rpc = client.Get<ILoadTestService>();
            foreach (var concurrency in options.ConcurrencyConfig)
            {
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"[Client] warmup {options.WarmupSeconds}s @ c={concurrency}");
                    _ = await ExecuteStageAsync(
                        rpc,
                        options.Operation,
                        options.PayloadSize,
                        options.WarmupSeconds,
                        concurrency,
                        metrics,
                        isWarmup: true);
                }

                var result = await ExecuteStageAsync(
                    rpc,
                    options.Operation,
                    options.PayloadSize,
                    options.DurationSeconds,
                    concurrency,
                    metrics,
                    isWarmup: false);

                Console.WriteLine(
                    $"[Result] op={result.Operation} c={result.Concurrency} qps={result.Qps:F2} ok={result.Success} fail={result.Failure} " +
                    $"err={result.ErrorRatePercent:F2}% p50={result.P50Us:F2}us p95={result.P95Us:F2}us p99={result.P99Us:F2}us p999={result.P999Us:F2}us " +
                    $"avg={result.AvgUs:F2}us min={result.MinUs:F2}us max={result.MaxUs:F2}us dur={result.ElapsedSeconds:F2}s");

                if (!string.IsNullOrEmpty(result.TopFailures))
                    Console.WriteLine($"[Failures] {result.TopFailures}");
                results.Add(result);
            }

            PerformanceReportWriter.Write(
                options.JsonOutputPath,
                "SharpLink.LoadTest",
                options,
                results);
        }
        finally
        {
            if (ownedClient is not null)
                await ownedClient.DisposeAsync();
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
        var realtimeHistogram = new LatencyHistogram(200_000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var token = cts.Token;
        var failures = new FailureRecorder();
        long success = 0;
        long failure = 0;
        long realtimeSuccess = 0;
        var workers = new Task[concurrency];
        var stageTimer = Stopwatch.StartNew();
        var lastRealtimeUpdate = stageTimer.Elapsed;
        var realtimeRef = realtimeHistogram;

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
                    var windowHistogram = Interlocked.Exchange(ref realtimeRef, new LatencyHistogram(200_000));

                    metrics.UpdateRealtime(new RealtimeResult(
                        operation,
                        concurrency,
                        windowSuccess / windowSeconds,
                        windowHistogram.Percentile(50),
                        windowHistogram.Percentile(95),
                        windowHistogram.Percentile(99),
                        windowHistogram.Percentile(99.9)));
                }
            }, CancellationToken.None);
        }

        for (var i = 0; i < workers.Length; i++)
        {
            var echoPayload = operation == "echo" ? new string('x', payloadSize) : string.Empty;
            workers[i] = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        if (operation == "echo")
                        {
                            _ = await rpc.EchoAsync(echoPayload);
                        }
                        else if (operation == "yield")
                        {
                            _ = await rpc.YieldAsync(7, 9);
                        }
                        else if (operation == "delay")
                        {
                            _ = await rpc.DelayAsync(7, 9);
                        }
                        else
                        {
                            _ = await rpc.AddAsync(7, 9);
                        }

                        var elapsedUs = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
                        histogram.Record(elapsedUs);
                        Volatile.Read(ref realtimeRef).Record(elapsedUs);
                        Interlocked.Increment(ref success);
                        Interlocked.Increment(ref realtimeSuccess);
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

        var workersTask = Task.WhenAll(workers);
        var gracefulStopTask = Task.Delay(TimeSpan.FromSeconds(durationSeconds + 5), CancellationToken.None);
        var completed = await Task.WhenAny(workersTask, gracefulStopTask);
        if (completed != workersTask)
            throw new TimeoutException("Load test stage did not stop in grace window; possible in-flight RPC stall.");

        await workersTask;
        if (realtimeReporter is not null)
            await realtimeReporter;

        var elapsedSeconds = Math.Max(0.001, stageTimer.Elapsed.TotalSeconds);
        var qps = success / elapsedSeconds;
        var total = success + failure;
        var errorRate = total == 0 ? 0 : failure * 100.0 / total;
        var result = new StageResult(
            operation,
            concurrency,
            success,
            failure,
            qps,
            histogram.Percentile(50),
            histogram.Percentile(95),
            histogram.Percentile(99),
            histogram.Percentile(99.9),
            histogram.Average,
            histogram.Min,
            histogram.Max,
            elapsedSeconds,
            errorRate,
            failures.Top(3));

        if (!isWarmup)
            metrics.UpdateStage(result);

        return result;
    }

}

public sealed class LoadTestOptions
{
    public RunMode Mode { get; private init; } = RunMode.Local;
    public TransportMode Transport { get; private init; } = TransportMode.Tcp;
    public string Host { get; private init; } = "127.0.0.1";
    public string BindIp { get; private init; } = "0.0.0.0";
    public int Port { get; private init; } = 19100;
    public string UdsPath { get; private init; } = TransportDefaults.GetDefaultUdsPath("sharplink-loadtest");
    public string PipeName { get; private init; } = TransportDefaults.GetDefaultPipeName("sharplink-loadtest");
    public int DurationSeconds { get; private init; } = 20;
    public int WarmupSeconds { get; private init; } = 5;
    public int[] ConcurrencyConfig { get; private init; } = [1, 2, 4, 8, 16, 32];
    public string Operation { get; private init; } = "add";
    public int PayloadSize { get; private init; } = 64;
    public int MetricsPort { get; private init; } = 9464;
    public int HeartbeatIntervalSeconds { get; private init; } = 10;
    public int HeartbeatCheckIntervalSeconds { get; private init; } = 10;
    public int HeartbeatTimeoutSeconds { get; private init; } = 120;
    public int MinConnections { get; private init; } = 1;
    public int MaxConnections { get; private init; } = 1;
    public SharpLinkPerformanceProfile PerformanceProfile { get; private init; } = SharpLinkPerformanceProfile.Balanced;
    public string RequestTimeoutMode { get; private init; } = "default";
    public string? JsonOutputPath { get; private init; }
    public bool DisableRequestTimeout => RequestTimeoutMode == "disabled";
    public TimeSpan? RequestTimeout => RequestTimeoutMode switch
    {
        "1ms" => TimeSpan.FromMilliseconds(1),
        "10ms" => TimeSpan.FromMilliseconds(10),
        "100ms" => TimeSpan.FromMilliseconds(100),
        _ => null
    };

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

        var transport = map.TryGetValue("transport", out var transportStr) && TransportDefaults.TryParseTransport(transportStr, out var parsedTransport)
            ? parsedTransport
            : TransportMode.Tcp;

        var concurrencyNum = map.TryGetValue("concurrency", out var concurrencyStr)
            ? concurrencyStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToArray()
            : [1, 2, 4, 8, 16, 32];

        var operation = map.GetValueOrDefault("operation", "add").ToLowerInvariant();
        if (operation is not ("add" or "echo" or "yield" or "delay"))
            throw new ArgumentException($"Unsupported operation: {operation}. Supported: add, echo, yield, delay.");

        var profileText = map.GetValueOrDefault("profile", "balanced");
        var profile = profileText.ToLowerInvariant() switch
        {
            "balanced" => SharpLinkPerformanceProfile.Balanced,
            "lowlatency" => SharpLinkPerformanceProfile.LowLatency,
            "throughput" => SharpLinkPerformanceProfile.Throughput,
            _ => throw new ArgumentException($"Unsupported performance profile: {profileText}.")
        };
        var requestTimeoutMode = map.GetValueOrDefault("request-timeout", "default").ToLowerInvariant();
        if (requestTimeoutMode is not ("default" or "disabled" or "1ms" or "10ms" or "100ms"))
            throw new ArgumentException($"Unsupported request timeout mode: {requestTimeoutMode}.");

        var minConnections = int.Parse(map.GetValueOrDefault("min-connections", "1"));
        var maxConnections = int.Parse(map.GetValueOrDefault("max-connections", "1"));
        var connectionPool = new SharpLinkConnectionPoolOptions
        {
            MinConnections = minConnections,
            MaxConnections = maxConnections
        };
        connectionPool.Validate();
        if (transport == TransportMode.AnonymousPipe && maxConnections != 1)
            throw new ArgumentException("Anonymous-pipe load tests require --max-connections 1.");

        return new LoadTestOptions
        {
            Mode = mode,
            Transport = transport,
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19100")),
            UdsPath = map.GetValueOrDefault("uds-path", TransportDefaults.GetDefaultUdsPath("sharplink-loadtest")),
            PipeName = map.GetValueOrDefault("pipe-name", TransportDefaults.GetDefaultPipeName("sharplink-loadtest")),
            DurationSeconds = int.Parse(map.GetValueOrDefault("duration", "20")),
            WarmupSeconds = int.Parse(map.GetValueOrDefault("warmup", "5")),
            ConcurrencyConfig = concurrencyNum.Length == 0 ? [1] : concurrencyNum,
            Operation = operation,
            PayloadSize = int.Parse(map.GetValueOrDefault("payload-size", "64")),
            MetricsPort = int.Parse(map.GetValueOrDefault("metrics-port", "9464")),
            HeartbeatIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-interval", "10")),
            HeartbeatCheckIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-check-interval", "10")),
            HeartbeatTimeoutSeconds = int.Parse(map.GetValueOrDefault("heartbeat-timeout", "120")),
            MinConnections = minConnections,
            MaxConnections = maxConnections,
            PerformanceProfile = profile,
            RequestTimeoutMode = requestTimeoutMode,
            JsonOutputPath = map.GetValueOrDefault("json-output")
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
    double P999Us,
    double AvgUs,
    double MinUs,
    double MaxUs,
    double ElapsedSeconds,
    double ErrorRatePercent,
    string TopFailures);

public sealed record RealtimeResult(
    string Operation,
    int Concurrency,
    double Qps,
    double P50Us,
    double P95Us,
    double P99Us,
    double P999Us);

internal sealed class LatencyHistogram
{
    private const int DefaultBucketCount = 2_000_000;
    private readonly long[] _buckets;
    private long _count;
    private long _sumUs;
    private long _minUs = long.MaxValue;
    private long _maxUs;

    public LatencyHistogram(int bucketCount = DefaultBucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);

        _buckets = new long[bucketCount];
    }

    public void Record(double microseconds)
    {
        var us = (long)Math.Max(0, Math.Round(microseconds));
        var bucket = (int)Math.Clamp(us, 0, _buckets.Length - 1);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumUs, us);
        UpdateMin(us);
        UpdateMax(us);
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

    public double Average
    {
        get
        {
            var count = Interlocked.Read(ref _count);
            if (count <= 0)
                return 0;
            return Interlocked.Read(ref _sumUs) / (double)count;
        }
    }

    public double Min
    {
        get
        {
            var value = Interlocked.Read(ref _minUs);
            return value == long.MaxValue ? 0 : value;
        }
    }

    public double Max => Interlocked.Read(ref _maxUs);

    private void UpdateMin(long value)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _minUs);
            if (value >= old)
                return;
            if (Interlocked.CompareExchange(ref _minUs, value, old) == old)
                return;
        }
    }

    private void UpdateMax(long value)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _maxUs);
            if (value <= old)
                return;
            if (Interlocked.CompareExchange(ref _maxUs, value, old) == old)
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
        if (_counts.IsEmpty)
            return string.Empty;

        return string.Join(", ", _counts
            .OrderByDescending(x => x.Value)
            .Take(count)
            .Select(x => $"{x.Key}:{x.Value}"));
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
        sb.AppendLine("# TYPE sharplink_load_test_stage_error_rate_percent gauge");
        sb.AppendLine("# TYPE sharplink_load_test_stage_latency_us gauge");
        foreach (var (concurrency, result) in _stageByConcurrency.OrderBy(x => x.Key))
        {
            sb.AppendLine($"sharplink_load_test_stage_qps{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\"}} {result.Qps:F2}");
            sb.AppendLine($"sharplink_load_test_stage_error_rate_percent{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\"}} {result.ErrorRatePercent:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.50\"}} {result.P50Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.95\"}} {result.P95Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.99\"}} {result.P99Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.999\"}} {result.P999Us:F2}");
            sb.AppendLine($"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"avg\"}} {result.AvgUs:F2}");
        }

        sb.AppendLine("# TYPE sharplink_load_test_realtime_qps gauge");
        sb.AppendLine("# TYPE sharplink_load_test_realtime_latency_us gauge");
        foreach (var (concurrency, result) in _realtimeByConcurrency.OrderBy(x => x.Key))
        {
            sb.AppendLine($"sharplink_load_test_realtime_qps{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\"}} {result.Qps:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.50\"}} {result.P50Us:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.95\"}} {result.P95Us:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.99\"}} {result.P99Us:F2}");
            sb.AppendLine($"sharplink_load_test_realtime_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"0.999\"}} {result.P999Us:F2}");
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
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
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
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (IsIgnorable(ex))
        {
        }

        _cts.Dispose();
    }

    private static bool IsIgnorable(AggregateException ex)
        => ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException or ObjectDisposedException or HttpListenerException);
}

[RpcContract]
public interface ILoadTestService : IService
{
    ValueTask<int> AddAsync(int left, int right);
    ValueTask<string> EchoAsync(string value);
    ValueTask<int> YieldAsync(int left, int right);
    ValueTask<int> DelayAsync(int left, int right);
}

[RpcService]
public class LoadTestService : ILoadTestService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);

    public async ValueTask<int> YieldAsync(int left, int right)
    {
        await Task.Yield();
        return left + right;
    }

    public async ValueTask<int> DelayAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        return left + right;
    }
}
