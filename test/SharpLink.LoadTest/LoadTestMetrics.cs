using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
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
using SharpLink.Server;

namespace SharpLink.LoadTest;

internal sealed class FailureRecorder
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _firstDetails = new(StringComparer.Ordinal);

    public void Record(Exception ex)
    {
        var key = ex is SharpLinkException sharpLink
            ? $"{nameof(SharpLinkException)}[{sharpLink.Code}]"
            : ex.GetType().Name;
        _counts.AddOrUpdate(key, 1, static (_, old) => old + 1);
        if (_firstDetails.TryAdd(key, ex.ToString()))
            Console.Error.WriteLine($"[FailureDetail:{key}] {ex}");
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
    private long _totalSendQueueBackpressureRetries;

    public void UpdateStage(StageResult result)
    {
        _stageByConcurrency[result.Concurrency] = result;
        Interlocked.Add(ref _totalSuccess, result.Success);
        Interlocked.Add(ref _totalFailure, result.Failure);
        Interlocked.Add(ref _totalSendQueueBackpressureRetries, result.SendQueueBackpressureRetries);
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
        sb.AppendLine("# TYPE sharplink_load_test_total_send_queue_backpressure_retries counter");
        sb.AppendLine(
            $"sharplink_load_test_total_send_queue_backpressure_retries {Interlocked.Read(ref _totalSendQueueBackpressureRetries)}");
        sb.AppendLine("# TYPE sharplink_load_test_stage_qps gauge");
        sb.AppendLine("# TYPE sharplink_load_test_stage_error_rate_percent gauge");
        sb.AppendLine("# TYPE sharplink_load_test_stage_latency_us gauge");
        foreach (var (concurrency, result) in _stageByConcurrency.OrderBy(x => x.Key))
        {
            sb.AppendLine($"sharplink_load_test_stage_qps{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\"}} {result.Qps:F2}");
            sb.AppendLine($"sharplink_load_test_stage_error_rate_percent{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\"}} {result.ErrorRatePercent:F2}");
            AppendLatency("0.50", result.P50Us);
            AppendLatency("0.95", result.P95Us);
            AppendLatency("0.99", result.P99Us);
            AppendLatency("0.999", result.P999Us);
            AppendLatency("avg", result.AvgUs);

            void AppendLatency(string quantile, double? value)
            {
                if (value.HasValue)
                {
                    sb.AppendLine(
                        $"sharplink_load_test_stage_latency_us{{concurrency=\"{concurrency}\",operation=\"{result.Operation}\",quantile=\"{quantile}\"}} {value.Value:F2}");
                }
            }
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
