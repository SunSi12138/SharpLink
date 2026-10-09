using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.OneWayEvidence;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 8)
        {
            Console.Error.WriteLine("Usage: <tcp|shm> <streams:0|1|2> <batch:1|8> <seconds> <warmup-seconds> <items> <features:none|comma-list> <output.json>");
            return 2;
        }
        var result = new EvidenceResult
        {
            Transport = args[0], Streams = int.Parse(args[1], CultureInfo.InvariantCulture),
            BatchSize = int.Parse(args[2], CultureInfo.InvariantCulture),
            RequestedSeconds = double.Parse(args[3], CultureInfo.InvariantCulture),
            WarmupSeconds = double.Parse(args[4], CultureInfo.InvariantCulture),
            ItemsPerStream = int.Parse(args[5], CultureInfo.InvariantCulture), Features = args[6],
            Arm = Environment.GetEnvironmentVariable("ONEWAY_ARM") ?? "unknown",
            SourceSha = Environment.GetEnvironmentVariable("ONEWAY_SOURCE_SHA") ?? "unknown"
        };
        try
        {
            if (result.Transport is not ("tcp" or "shm") || result.Streams is < 0 or > 2
                || result.BatchSize is < 1 or > 1024 || result.RequestedSeconds <= 0 || result.WarmupSeconds < 0 || result.ItemsPerStream < 1)
                throw new ArgumentException("Invalid scenario bounds.");
            foreach (var feature in result.Features.Split(','))
                if (feature is not ("none" or "deadline" or "token" or "interceptor" or "telemetry" or "endpoint" or "admission"))
                    throw new ArgumentException($"Unknown feature {feature}.");
            await MeasureAsync(result).ConfigureAwait(false);
            result.Status = "passed";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.ToString();
            Console.Error.WriteLine(error);
        }
        var json = JsonSerializer.Serialize(result, EvidenceJsonContext.Default.EvidenceResult);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[7]))!);
        await File.WriteAllTextAsync(args[7], json).ConfigureAwait(false);
        Console.WriteLine(json);
        return result.Status == "passed" ? 0 : 1;
    }

    private static async Task MeasureAsync(EvidenceResult result)
    {
        bool Has(string name) => Array.IndexOf(result.Features.Split(','), name) >= 0;
        using var telemetry = new EvidenceFeatures(Has("telemetry"));
        using var userCancellation = new CancellationTokenSource();
        var token = Has("token") ? userCancellation.Token : CancellationToken.None;
        var barrier = new CompletionBarrier(result.BatchSize);
        var service = new EvidenceService
        {
            Barrier = barrier,
            ItemCount = result.ItemsPerStream
        };
        var serverBuilder = SharpLinkServerBuilder.Create().UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var name = $"oneway-evidence-{Guid.NewGuid():N}";
        var port = 0;
        if (result.Transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        }
        else
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name));
        serverBuilder.ReplaceService<IOneWayEvidence>(service);
        if (Has("interceptor"))
            serverBuilder.AddInterceptor(new EvidenceFeatures.ServerInterceptor(telemetry));
        await using var server = serverBuilder.Build();
        await server.StartAsync().ConfigureAwait(false);
        var clientBuilder = SharpClientBuilder.Create().UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        if (Has("endpoint") || Has("admission"))
        {
            var address = result.Transport == "tcp"
                ? (SharpLinkTransportAddress)new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
                : new SharpLinkSharedMemoryAddress(name);
            clientBuilder.UseEndpoints([new SharpLinkEndpoint { Id = "only", Address = address }],
                result.Transport == "tcp" ? SharpLinkTransportFactories.Sockets() : SharpLinkTransportFactories.SharedMemory());
            if (Has("admission"))
                clientBuilder.UseEndpointAdmission(new EvidenceFeatures.Admission(telemetry));
        }
        else if (result.Transport == "tcp")
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        else
            clientBuilder.UseSharedMemory(name);
        if (Has("deadline"))
            clientBuilder.UseRequestTimeout(TimeSpan.FromMinutes(2));
        else
            clientBuilder.DisableRequestTimeout();
        if (Has("interceptor"))
            clientBuilder.AddInterceptor(new EvidenceFeatures.ClientInterceptor(telemetry));
        await using var client = clientBuilder.Build();
        await client.ConnectAsync().ConfigureAwait(false);
        var rpc = client.Get<IOneWayEvidence>();
        long nextCall = 0;

        async ValueTask BatchAsync()
        {
            var completed = barrier.Begin(nextCall);
            for (var slot = 0; slot < result.BatchSize; slot++)
            {
                var call = nextCall++;
                switch (result.Streams)
                {
                    case 0:
                        await rpc.ZeroAsync(call, token).ConfigureAwait(false);
                        break;
                    case 1:
                        await rpc.OneAsync(call, EvidenceService.Items(call, 0, result.ItemsPerStream), token).ConfigureAwait(false);
                        break;
                    default:
                        await rpc.TwoAsync(call, EvidenceService.Items(call, 0, result.ItemsPerStream),
                            EvidenceService.Items(call, 1, result.ItemsPerStream), token).ConfigureAwait(false);
                        break;
                }
            }
            await completed.ConfigureAwait(false);
        }

        // Verify selected lifetime source in one untimed call, then remove the probe
        // listener before warmup. Server cancellation tokens also include shutdown,
        // so CanBeCanceled alone would not prove that a deadline reached the call.
        var detail = client.GetTelemetryDetailPolicySnapshot().Mode;
        client.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Detailed);
        using (var probe = new ActivityListener
        {
            ShouldListenTo = static source => ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => result.ProbeLifetimeSource = activity.GetTagItem("rpc.sharplink.lifetime_source") as string
        })
        {
            ActivitySource.AddActivityListener(probe);
            await BatchAsync().ConfigureAwait(false);
        }
        client.UpdateTelemetryDetailPolicy(detail);
        if (result.ProbeLifetimeSource != (Has("deadline") ? "client_custom_timeout" : null))
            throw new InvalidOperationException($"Unexpected selected lifetime source: {result.ProbeLifetimeSource ?? "none"}.");

        var warmupStart = Stopwatch.GetTimestamp();
        do { await BatchAsync().ConfigureAwait(false); }
        while (Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < result.WarmupSeconds);
        result.WarmupCalls = nextCall;
        result.WarmupActivities = telemetry.Activities;
        result.WarmupMeasurements = telemetry.Measurements;
        result.WarmupClientInterceptions = telemetry.ClientInterceptions;
        result.WarmupServerInterceptions = telemetry.ServerInterceptions;
        result.WarmupAdmissionAcquisitions = telemetry.AdmissionAcquisitions;
        if ((Has("telemetry") && (telemetry.Activities == 0 || telemetry.Measurements == 0))
            || (Has("interceptor") && (telemetry.ClientInterceptions == 0 || telemetry.ServerInterceptions == 0))
            || (Has("admission") && telemetry.AdmissionAcquisitions == 0))
            throw new InvalidOperationException("Requested feature did not activate during warmup.");
        await Task.Delay(200).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var callsBefore = barrier.CompletedCalls;
        var itemsBefore = barrier.ValidatedItems;
        var gen0 = GC.CollectionCount(0);
        var start = Stopwatch.GetTimestamp();
        do { await BatchAsync().ConfigureAwait(false); }
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < result.RequestedSeconds);
        result.ElapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        process.Refresh();
        result.CpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        result.AllocatedBytes = allocatedAfter - allocatedBefore;
        result.CompletedCalls = barrier.CompletedCalls - callsBefore;
        result.ValidatedItems = barrier.ValidatedItems - itemsBefore;
        result.Gen0Collections = GC.CollectionCount(0) - gen0;
        if (result.CompletedCalls != nextCall - result.WarmupCalls
            || result.ValidatedItems != result.CompletedCalls * result.Streams * result.ItemsPerStream)
            throw new InvalidOperationException("Server completion or item-integrity totals do not match issued work.");
        result.NsPerCall = result.ElapsedSeconds * 1e9 / result.CompletedCalls;
        result.CpuNsPerCall = result.CpuSeconds * 1e9 / result.CompletedCalls;
        result.AllocatedBytesPerCall = result.AllocatedBytes / (double)result.CompletedCalls;
        result.CallsPerSecond = result.CompletedCalls / result.ElapsedSeconds;
        // Same post-measurement drain/settle boundary for every arm; no outstanding input streams.
        await Task.Delay(200).ConfigureAwait(false);
        await client.StopAsync().ConfigureAwait(false);
        await server.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}

internal sealed class EvidenceResult
{
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string Arm { get; set; } = "";
    public string SourceSha { get; set; } = "";
    public string Transport { get; set; } = "";
    public int Streams { get; set; }
    public int BatchSize { get; set; }
    public int ItemsPerStream { get; set; }
    public string Features { get; set; } = "";
    public double RequestedSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public long WarmupCalls { get; set; }
    public long CompletedCalls { get; set; }
    public long ValidatedItems { get; set; }
    public double ElapsedSeconds { get; set; }
    public double CpuSeconds { get; set; }
    public long AllocatedBytes { get; set; }
    public int Gen0Collections { get; set; }
    public string? ProbeLifetimeSource { get; set; }
    public long WarmupActivities { get; set; }
    public long WarmupMeasurements { get; set; }
    public long WarmupClientInterceptions { get; set; }
    public long WarmupServerInterceptions { get; set; }
    public long WarmupAdmissionAcquisitions { get; set; }
    public double NsPerCall { get; set; }
    public double CpuNsPerCall { get; set; }
    public double AllocatedBytesPerCall { get; set; }
    public double CallsPerSecond { get; set; }
    public string Runtime { get; set; } = RuntimeInformation.FrameworkDescription;
    public string OS { get; set; } = RuntimeInformation.OSDescription;
    public string Architecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
    public bool ServerGc { get; set; } = GCSettings.IsServerGC;
    public int ProcessorCount { get; set; } = Environment.ProcessorCount;
    public string Host { get; set; } = Environment.MachineName;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

[JsonSerializable(typeof(EvidenceResult))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class EvidenceJsonContext : JsonSerializerContext;
