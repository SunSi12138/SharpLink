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

public sealed class LoadTestOptions
{
    public RunMode Mode { get; private init; } = RunMode.Local;
    public TransportMode Transport { get; private init; } = TransportMode.Tcp;
    public string Host { get; private init; } = "127.0.0.1";
    public string BindIp { get; private init; } = "0.0.0.0";
    public int Port { get; private init; } = 19100;
    public string UdsPath { get; private init; } = TransportDefaults.GetDefaultUdsPath("sharplink-loadtest");
    public string PipeName { get; private init; } = TransportDefaults.GetDefaultPipeName("sharplink-loadtest");
    public string SharedMemoryName { get; private init; } = TransportDefaults.GetDefaultSharedMemoryName("sharplink-loadtest");
    public int? SharedMemoryCapacity { get; private init; }
    public int? SharedMemorySpinCount { get; private init; }
    public bool DetailedSharedMemoryEvidence { get; private init; }
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
    public int ClientCount { get; private init; } = 1;
    public int ConcurrencyPerClient { get; private init; } = 1024;
    public int HoldDurationSeconds { get; private init; } = 30;
    public int MaxConcurrentCallsPerConnection { get; private init; } = 1024;
    public int MaxConcurrentCallsPerServer { get; private init; } = SharpLinkFlowControlOptions.DefaultMaxConcurrentCallsPerServer;
    public int MaxPendingRequestsPerConnection { get; private init; } = 65_536;
    public bool UseStaticEndpoints { get; private init; }
    public int StaticEndpointCount { get; private init; } = 1;
    public bool UseDynamicResolver { get; private init; }
    public int DynamicEndpointCount { get; private init; } = 1;
    public int EndpointCount => UseDynamicResolver ? DynamicEndpointCount : StaticEndpointCount;
    public SharpLinkLoadBalancingStrategy StaticLoadBalancingStrategy { get; private init; } = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices;
    public SharpLinkPerformanceProfile PerformanceProfile { get; private init; } = SharpLinkPerformanceProfile.Balanced;
    public string RequestTimeoutMode { get; private init; } = "default";
    public string AdmissionMode { get; private init; } = "disabled";
    public int? MaxSendQueueBytes { get; private init; }
    public string PayloadPattern { get; private init; } = "compressible";
    public string? JsonOutputPath { get; private init; }
    public LatencyRecordingMode RecordingMode { get; private init; } = LatencyRecordingMode.Formal;
    public int MaximumRecordedOperations { get; private init; } = 30_000_000;
    public int DrainTimeoutSeconds { get; private init; } = 5;
    public bool TailObserver { get; private init; }
    public int TailObserverMaximumRecordedOperations => MaximumRecordedOperations;
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
        var staticEndpointCount = int.Parse(map.GetValueOrDefault("static-endpoints", "1"));
        if (staticEndpointCount is < 1 or > SharpLinkClusterOptions.MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(staticEndpointCount));
        var useStaticEndpoints = map.ContainsKey("static-endpoints");
        var dynamicEndpointCount = int.Parse(map.GetValueOrDefault("dynamic-endpoints", "1"));
        if (dynamicEndpointCount is < 1 or > SharpLinkClusterOptions.MaximumEndpoints)
            throw new ArgumentOutOfRangeException(nameof(dynamicEndpointCount));
        var useDynamicResolver = map.ContainsKey("dynamic-endpoints");
        if (useStaticEndpoints && useDynamicResolver)
            throw new ArgumentException("Static and dynamic endpoint load-test modes are mutually exclusive.");
        if ((useStaticEndpoints || useDynamicResolver) && (mode != RunMode.Local || transport != TransportMode.Tcp))
        {
            throw new ArgumentException(
                "Endpoint topology load tests currently support only --mode local --transport tcp.");
        }
        var staticLoadBalancingStrategy = map.GetValueOrDefault("load-balancing", "p2c").ToLowerInvariant() switch
        {
            "p2c" => SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
            "random" => SharpLinkLoadBalancingStrategy.Random,
            "roundrobin" => SharpLinkLoadBalancingStrategy.RoundRobin,
            "leastpending" => SharpLinkLoadBalancingStrategy.LeastPending,
            _ => throw new ArgumentException("Unsupported static load-balancing strategy.")
        };

        var concurrencyNum = map.TryGetValue("concurrency", out var concurrencyStr)
            ? concurrencyStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToArray()
            : [1, 2, 4, 8, 16, 32];

        var operation = map.GetValueOrDefault("operation", "add").ToLowerInvariant();
        if (operation is not ("empty" or "add" or "echo" or "oneway" or "yield" or "delay" or "hold"))
            throw new ArgumentException(
                $"Unsupported operation: {operation}. Supported: empty, add, echo, oneway, yield, delay, hold.");

        var profileText = map.GetValueOrDefault("profile", "balanced");
        var profile = profileText.ToLowerInvariant() switch
        {
            "balanced" => SharpLinkPerformanceProfile.Balanced,
            "lowlatency" => SharpLinkPerformanceProfile.LowLatency,
            "throughput" => SharpLinkPerformanceProfile.Throughput,
            _ => throw new ArgumentException($"Unsupported performance profile: {profileText}.")
        };
        var requestTimeoutMode = map.GetValueOrDefault(
            "request-timeout",
            operation == "hold" ? "disabled" : "default").ToLowerInvariant();
        if (requestTimeoutMode is not ("default" or "disabled" or "1ms" or "10ms" or "100ms"))
            throw new ArgumentException($"Unsupported request timeout mode: {requestTimeoutMode}.");
        var admissionMode = map.GetValueOrDefault("admission", "disabled").ToLowerInvariant();
        if (admissionMode is not ("disabled" or "immediate" or "queue" or "reject"))
            throw new ArgumentException($"Unsupported admission mode: {admissionMode}.");
        var maxSendQueueBytes = ParseOptionalInt(map, "max-send-queue-bytes");
        if (maxSendQueueBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSendQueueBytes));
        var payloadPattern = map.GetValueOrDefault("payload-pattern", "compressible").ToLowerInvariant();
        if (payloadPattern is not ("compressible" or "random"))
            throw new ArgumentException($"Unsupported payload pattern: {payloadPattern}.");
        var recordingModeText = map.GetValueOrDefault("recording", "formal").ToLowerInvariant();
        var recordingMode = recordingModeText switch
        {
            "off" => LatencyRecordingMode.Off,
            "formal" => LatencyRecordingMode.Formal,
            "diagnostic" => LatencyRecordingMode.Diagnostic,
            "validation-dual" => LatencyRecordingMode.ValidationDual,
            _ => throw new ArgumentException($"Unsupported recording mode: {recordingModeText}.")
        };
        var maximumRecordedOperations = int.Parse(
            map.GetValueOrDefault("maximum-recorded-operations", "30000000"),
            CultureInfo.InvariantCulture);
        if (maximumRecordedOperations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordedOperations));
        var drainTimeoutSeconds = int.Parse(
            map.GetValueOrDefault("drain-timeout", "5"),
            CultureInfo.InvariantCulture);
        if (drainTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(drainTimeoutSeconds));
        var tailObserver = map.TryGetValue("tail-observer", out var tailObserverText) &&
                           bool.Parse(tailObserverText);
        if (tailObserver && operation != "add")
            throw new ArgumentException("The tail observer currently requires --operation add.");
        if (tailObserver && transport != TransportMode.Tcp)
            throw new ArgumentException("The tail observer currently requires --transport tcp.");
        if (tailObserver && (useStaticEndpoints || useDynamicResolver))
        {
            throw new ArgumentException(
                "The tail observer requires a fixed TCP endpoint and cannot be combined with endpoint topology mode.");
        }

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
        if (recordingMode is LatencyRecordingMode.Formal or LatencyRecordingMode.ValidationDual &&
            concurrencyNum.Any(concurrency => maximumRecordedOperations < concurrency))
        {
            throw new ArgumentException(
                "Formal recording capacity must provide at least one sample slot per configured worker.");
        }

        var clientCount = int.Parse(map.GetValueOrDefault("client-count", operation == "hold" ? "4" : "1"));
        if (clientCount is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(clientCount));
        var concurrencyPerClient = int.Parse(map.GetValueOrDefault("concurrency-per-client", "1024"));
        if (concurrencyPerClient is < 1 or > SharpLinkProtocolOptions.MaximumPendingRequestsPerConnection)
            throw new ArgumentOutOfRangeException(nameof(concurrencyPerClient));
        var holdDurationSeconds = int.Parse(map.GetValueOrDefault("hold-duration", "30"));
        if (holdDurationSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(holdDurationSeconds));
        var maxConcurrentCallsPerConnection = int.Parse(
            map.GetValueOrDefault("max-concurrent-calls-per-connection", "1024"));
        var maxConcurrentCallsPerServer = int.Parse(
            map.GetValueOrDefault(
                "max-concurrent-calls-per-server",
                SharpLinkFlowControlOptions.DefaultMaxConcurrentCallsPerServer.ToString(CultureInfo.InvariantCulture)));
        var maxPendingRequestsPerConnection = int.Parse(
            map.GetValueOrDefault("max-pending-requests-per-connection", "65536"));
        new SharpLinkFlowControlOptions
        {
            MaxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection,
            MaxConcurrentCallsPerServer = maxConcurrentCallsPerServer
        }.Validate();
        new SharpLinkProtocolOptions
        {
            MaxPendingRequestsPerConnection = maxPendingRequestsPerConnection
        }.Validate();
        if (operation == "hold")
        {
            if (transport == TransportMode.AnonymousPipe)
                throw new ArgumentException("The hold operation requires a transport that supports independent clients.");
            if (minConnections != 1 || maxConnections != 1)
                throw new ArgumentException("The hold operation requires exactly one connection per client so pooled routing cannot mask call capacity.");
            if (useStaticEndpoints || useDynamicResolver)
                throw new ArgumentException("The hold operation measures one server instance and cannot use endpoint-topology mode.");
            if (admissionMode != "disabled")
                throw new ArgumentException("The hold operation requires --admission disabled so admission limits do not mask call capacity.");
            if (requestTimeoutMode != "disabled")
                throw new ArgumentException("The hold operation requires --request-timeout disabled so client deadlines cannot expire before gate release.");
            var attemptedCalls = checked(clientCount * concurrencyPerClient);
            if (attemptedCalls > SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(concurrencyPerClient),
                    $"The hold operation supports at most {SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer} attempted calls per run.");
            }
        }
        var sharedMemoryCapacity = ParseOptionalInt(map, "shm-capacity");
        var sharedMemorySpinCount = ParseOptionalInt(map, "shm-spin-count");
        if (transport == TransportMode.SharedMemory)
        {
            new SharedMemoryTransportOptions
            {
                CapacityPerDirectionBytes = sharedMemoryCapacity,
                SpinCount = sharedMemorySpinCount
            }.Validate();
        }

        return new LoadTestOptions
        {
            Mode = mode,
            Transport = transport,
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19100")),
            UdsPath = map.GetValueOrDefault("uds-path", TransportDefaults.GetDefaultUdsPath("sharplink-loadtest")),
            PipeName = map.GetValueOrDefault("pipe-name", TransportDefaults.GetDefaultPipeName("sharplink-loadtest")),
            SharedMemoryName = map.GetValueOrDefault("shm-name", TransportDefaults.GetDefaultSharedMemoryName("sharplink-loadtest")),
            SharedMemoryCapacity = sharedMemoryCapacity,
            SharedMemorySpinCount = sharedMemorySpinCount,
            DetailedSharedMemoryEvidence = map.TryGetValue("detailed-shm-evidence", out var detailedEvidence) &&
                                           bool.Parse(detailedEvidence),
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
            ClientCount = clientCount,
            ConcurrencyPerClient = concurrencyPerClient,
            HoldDurationSeconds = holdDurationSeconds,
            MaxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection,
            MaxConcurrentCallsPerServer = maxConcurrentCallsPerServer,
            MaxPendingRequestsPerConnection = maxPendingRequestsPerConnection,
            UseStaticEndpoints = useStaticEndpoints,
            StaticEndpointCount = staticEndpointCount,
            UseDynamicResolver = useDynamicResolver,
            DynamicEndpointCount = dynamicEndpointCount,
            StaticLoadBalancingStrategy = staticLoadBalancingStrategy,
            PerformanceProfile = profile,
            RequestTimeoutMode = requestTimeoutMode,
            AdmissionMode = admissionMode,
            MaxSendQueueBytes = maxSendQueueBytes,
            PayloadPattern = payloadPattern,
            JsonOutputPath = map.GetValueOrDefault("json-output"),
            RecordingMode = recordingMode,
            MaximumRecordedOperations = maximumRecordedOperations,
            DrainTimeoutSeconds = drainTimeoutSeconds,
            TailObserver = tailObserver
        };
    }

    private static int? ParseOptionalInt(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) ? int.Parse(value) : null;

}
