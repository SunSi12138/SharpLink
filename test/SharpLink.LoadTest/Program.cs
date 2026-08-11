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

public static class Program
{
    private static PerformanceEvidenceCollector? s_evidenceCollector;

    public static async Task Main(string[] args)
    {
        if (args.Any(static x => string.Equals(x, "--help", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(x, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return;
        }

        var options = LoadTestOptions.Parse(args);
        using var evidenceCollector = new PerformanceEvidenceCollector(options.DetailedSharedMemoryEvidence);
        s_evidenceCollector = evidenceCollector;
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
            $"staticEndpoints={options.StaticEndpointCount} dynamicEndpoints={options.DynamicEndpointCount} dynamicResolver={options.UseDynamicResolver} lb={options.StaticLoadBalancingStrategy} " +
            $"profile={options.PerformanceProfile} requestTimeout={options.RequestTimeoutMode} " +
            $"admission={options.AdmissionMode} compression={options.CompressionAlgorithm}/{options.CompressionLevel} " +
            $"thresholds={options.CompressionMinimumPayloadBytes}B/{options.CompressionMinimumSavingsBytes}B/{options.CompressionMinimumSavingsRatio:P0} " +
            $"sendQueue={options.MaxSendQueueBytes?.ToString(CultureInfo.InvariantCulture) ?? "profile-default"}B " +
            $"pattern={options.PayloadPattern} recording={options.RecordingMode} " +
            $"sampleCapacity={options.MaximumRecordedOperations} drainTimeout={options.DrainTimeoutSeconds}s " +
            $"tailObserver={options.TailObserver}");
        if (options.Operation == "hold")
        {
            Console.WriteLine(
                $"[Config] clients={options.ClientCount} connections/client={options.MinConnections} " +
                $"concurrency/client={options.ConcurrencyPerClient} hold={options.HoldDurationSeconds}s " +
                $"callCapacity={options.MaxConcurrentCallsPerConnection}/{options.MaxConcurrentCallsPerServer} " +
                $"pendingCapacity={options.MaxPendingRequestsPerConnection}");
        }

        if (options.Transport == TransportMode.Tcp)
            Console.WriteLine($"[Config] tcp://{options.Host}:{options.Port} (bind={options.BindIp})");
        else if (options.Transport == TransportMode.Uds)
            Console.WriteLine($"[Config] uds://{options.UdsPath}");
        else if (options.Transport == TransportMode.NamedPipe)
            Console.WriteLine($"[Config] pipe://{options.PipeName}");
        else if (options.Transport == TransportMode.SharedMemory)
            Console.WriteLine($"[Config] shm://{options.SharedMemoryName} capacity={options.SharedMemoryCapacity?.ToString() ?? "profile"} spin={options.SharedMemorySpinCount?.ToString() ?? "profile"}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.LoadTest options:");
        Console.WriteLine("  --mode local|server|client");
        Console.WriteLine("  --transport tcp|uds|namedpipe|anonymous|sharedmemory");
        Console.WriteLine("  --host 127.0.0.1 --bind-ip 0.0.0.0 --port 19100");
        Console.WriteLine("  --duration 20 --warmup 5 --concurrency 1,2,4,8,16,32");
        Console.WriteLine("  --operation empty|add|echo|oneway|yield|delay|hold --payload-size 64");
        Console.WriteLine("  --client-count 4 --concurrency-per-client 2048 --hold-duration 30 (hold only)");
        Console.WriteLine("  --max-concurrent-calls-per-connection 2048 --max-concurrent-calls-per-server 32768");
        Console.WriteLine("  --max-pending-requests-per-connection 65536");
        Console.WriteLine("  --min-connections 1 --max-connections 1");
        Console.WriteLine("  --static-endpoints 1 | --dynamic-endpoints 1 --load-balancing p2c|random|roundrobin|leastpending (local TCP only)");
        Console.WriteLine("  --profile balanced|lowlatency|throughput");
        Console.WriteLine("  --request-timeout default|disabled|1ms|10ms|100ms");
        Console.WriteLine("  --admission disabled|immediate|queue|reject");
        Console.WriteLine("  --compression none|brotli --compression-level fastest|optimal|smallest|nocompression");
        Console.WriteLine("  --compression-min-payload 1024 --compression-min-savings-bytes 64 --compression-min-savings-ratio 0.05");
        Console.WriteLine("  --max-send-queue-bytes 33554432 (optional bounded throughput-test override)");
        Console.WriteLine("  --payload-pattern compressible|random");
        Console.WriteLine("  --shm-name sharplink-loadtest --shm-capacity 8388608 --shm-spin-count 8");
        Console.WriteLine("  --detailed-shm-evidence (diagnostic counters; do not use for formal timing)");
        Console.WriteLine("  --recording off|formal|diagnostic|validation-dual");
        Console.WriteLine("  --maximum-recorded-operations 30000000 --drain-timeout 5");
        Console.WriteLine("  --tail-observer (dedicated Add probe used only by the recorder interference gate)");
        Console.WriteLine("  --json-output artifacts/perf/load.json");
        Console.WriteLine("  --metrics-port 9464");
        Console.WriteLine("  --heartbeat-interval 10 --heartbeat-check-interval 10 --heartbeat-timeout 120");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport tcp");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport namedpipe");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport uds");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport anonymous");
        Console.WriteLine("  dotnet run --project test/SharpLink.LoadTest -- --mode local --transport sharedmemory");
    }

    private static async Task RunLocalAsync(LoadTestOptions options, MetricsRegistry metrics)
    {
        if (options.UseStaticEndpoints || options.UseDynamicResolver)
        {
            await RunStaticTcpLocalAsync(options, metrics);
            return;
        }

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
            builder => ConfigureServer(builder, options),
            options.PerformanceProfile,
            options.DisableRequestTimeout,
            options.RequestTimeout,
            options.SharedMemoryName,
            options.SharedMemoryCapacity,
            options.SharedMemorySpinCount,
            runtime => ConfigureRuntime(runtime, options),
            runtime => ConfigureRuntime(runtime, options));
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

    private static async Task RunStaticTcpLocalAsync(LoadTestOptions options, MetricsRegistry metrics)
    {
        var servers = new ISharpLinkServer?[options.EndpointCount];
        var serverTasks = new Task?[servers.Length];
        var endpoints = new SharpLinkEndpoint[servers.Length];
        using var serverCancellation = new CancellationTokenSource();
        try
        {
            for (var index = 0; index < servers.Length; index++)
            {
                var builder = ConfigureServer(SharpLinkServerBuilder.Create(), options)

                    .UseRuntime(runtime =>
                    {
                        runtime.PerformanceProfile = options.PerformanceProfile;
                        ConfigureRuntime(runtime, options);
                    })
                    .UseHeartbeat(
                        TimeSpan.FromSeconds(options.HeartbeatCheckIntervalSeconds),
                        TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
                    .UseTcp(0, options.BindIp);
                var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
                servers[index] = builder.Build();
                endpoints[index] = new SharpLinkEndpoint
                {
                    Id = $"local-{index}",
                    Address = new SharpLinkTcpAddress(options.Host, port)
                };
                serverTasks[index] = RunServerLoopAsync(servers[index]!, serverCancellation.Token);
            }

            await Task.Delay(200, serverCancellation.Token);
            var clientBuilder = SharpClientBuilder.Create()

                .UseRuntime(runtime =>
                {
                    runtime.PerformanceProfile = options.PerformanceProfile;
                    ConfigureRuntime(runtime, options);
                })
                .UseHeartbeat(
                    TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds),
                    TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds));
            if (options.UseDynamicResolver)
            {
                var snapshot = new SharpLinkEndpointSnapshot(1, endpoints);
                clientBuilder
                    .UseEndpointResolver(
                        new DelegateSharpLinkEndpointResolver(_ => ValueTask.FromResult(snapshot)),
                        SharpLinkTransportFactories.Sockets())
                    .UseCluster(cluster =>
                    {
                        cluster.MinReadyEndpoints = endpoints.Length;
                        cluster.MaxConnections = endpoints.Length;
                        cluster.MaxConnectionsPerEndpoint = 1;
                    })
                    .UseLoadBalancing(options.StaticLoadBalancingStrategy);
            }
            else if (endpoints.Length == 1)
            {
                clientBuilder.UseEndpoint(endpoints[0], SharpLinkTransportFactories.Sockets());
            }
            else
            {
                clientBuilder
                    .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
                    .UseCluster(cluster =>
                    {
                        cluster.MinReadyEndpoints = endpoints.Length;
                        cluster.MaxConnections = endpoints.Length;
                        cluster.MaxConnectionsPerEndpoint = 1;
                    })
                    .UseLoadBalancing(options.StaticLoadBalancingStrategy);
            }
            if (options.DisableRequestTimeout)
                clientBuilder.DisableRequestTimeout();
            else if (options.RequestTimeout is { } requestTimeout)
                clientBuilder.UseRequestTimeout(requestTimeout);
            await using var client = clientBuilder.Build();
            await RunClientOnlyAsync(options, metrics, client);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            for (var index = 0; index < servers.Length; index++)
            {
                var server = servers[index];
                if (server is not null)
                    await server.DisposeAsync();
            }
            await Task.WhenAll(serverTasks.Select(static task => task ?? Task.CompletedTask));
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ServerFailure] {exception}");
            throw;
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
            builder => ConfigureServer(builder, options),
            options.PerformanceProfile,
            options.SharedMemoryName,
            options.SharedMemoryCapacity,
            options.SharedMemorySpinCount,
            runtime => ConfigureRuntime(runtime, options));
        Console.WriteLine("[Server] started.");
        await server.RunAsync(cancelScope.Token);
    }

    private static async Task RunClientOnlyAsync(LoadTestOptions options, MetricsRegistry metrics, ISharpLinkClient? clientOverride = null)
    {
        if (options.Operation == "hold")
        {
            var result = await HoldCapacityRunner.RunAsync(options, clientOverride);
            PerformanceReportWriter.Write(
                options.JsonOutputPath,
                "SharpLink.LoadTest.HoldCapacity",
                options,
                [result],
                LoadTestJsonContext.Default);
            return;
        }

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
                options.RequestTimeout,
                options.SharedMemoryName,
                options.SharedMemoryCapacity,
                options.SharedMemorySpinCount,
                runtime => ConfigureRuntime(runtime, options))
            : null;
        var client = clientOverride ?? ownedClient!;
        var tailObserverClient = options.TailObserver
            ? LoadTestTransportFactory.CreateClient(
                options.Transport,
                options.Host,
                options.Port,
                options.UdsPath,
                options.PipeName,
                options.HeartbeatIntervalSeconds,
                options.HeartbeatTimeoutSeconds,
                1,
                1,
                options.PerformanceProfile,
                options.DisableRequestTimeout,
                options.RequestTimeout,
                options.SharedMemoryName,
                options.SharedMemoryCapacity,
                options.SharedMemorySpinCount,
                runtime => ConfigureRuntime(runtime, options))
            : null;
        var results = new List<StageResult>();
        try
        {
            await client.ConnectAsync();
            if (tailObserverClient is not null)
                await tailObserverClient.ConnectAsync();

            var rpc = client.Get<ILoadTestService>();
            var tailObserverRpc = tailObserverClient?.Get<ILoadTestService>();
            var retryOneWaySendQueueBackpressure =
                options.Operation == "oneway" && options.MaxSendQueueBytes.HasValue;
            foreach (var concurrency in options.ConcurrencyConfig)
            {
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"[Client] warmup {options.WarmupSeconds}s @ c={concurrency}");
                    _ = await ExecuteStageAsync(
                        rpc,
                        tailObserverRpc,
                        options,
                        options.WarmupSeconds,
                        concurrency,
                        metrics,
                        retryOneWaySendQueueBackpressure,
                        isWarmup: true);
                }

                var result = await ExecuteStageAsync(
                    rpc,
                    tailObserverRpc,
                    options,
                    options.DurationSeconds,
                    concurrency,
                    metrics,
                    retryOneWaySendQueueBackpressure,
                    isWarmup: false);

                Console.WriteLine(
                    $"[Result] op={result.Operation} c={result.Concurrency} qps={result.Qps:F2} ok={result.Success} fail={result.Failure} " +
                    $"sendQueueRetries={result.SendQueueBackpressureRetries} " +
                    $"err={result.ErrorRatePercent:F2}% p50={FormatLatency(result.P50Us)} p95={FormatLatency(result.P95Us)} p99={FormatLatency(result.P99Us)} p999={FormatLatency(result.P999Us)} " +
                    $"avg={FormatLatency(result.AvgUs)} min={FormatLatency(result.MinUs)} max={FormatLatency(result.MaxUs)} measurement={result.MeasurementDurationSeconds:F2}s drain={result.DrainDurationSeconds:F3}s " +
                    $"payload={result.OneWayPayloadMegabytesPerSecond:F2}/{result.RoundTripPayloadMegabytesPerSecond:F2} MiB/s(one-way/round-trip)");

                if (!string.IsNullOrEmpty(result.TopFailures))
                    Console.WriteLine($"[Failures] {result.TopFailures}");
                results.Add(result);
            }

            PerformanceReportWriter.Write(
                options.JsonOutputPath,
                "SharpLink.LoadTest",
                options,
                results,
                LoadTestJsonContext.Default);
        }
        finally
        {
            if (ownedClient is not null)
                await ownedClient.DisposeAsync();
            if (tailObserverClient is not null)
                await tailObserverClient.DisposeAsync();
        }
    }

    private static async Task<StageResult> ExecuteStageAsync(
        ILoadTestService rpc,
        ILoadTestService? tailObserverRpc,
        LoadTestOptions options,
        int durationSeconds,
        int concurrency,
        MetricsRegistry metrics,
        bool retryOneWaySendQueueBackpressure,
        bool isWarmup)
    {
        var operation = options.Operation;
        var recordingMode = isWarmup ? LatencyRecordingMode.Off : options.RecordingMode;
        var formalRecorder = LatencyRecordingPolicy.CreatesFormalRecorder(recordingMode)
            ? new StageLatencyRecorder(concurrency, options.MaximumRecordedOperations)
            : null;
        var diagnosticHistogram = LatencyRecordingPolicy.CreatesDiagnosticRecorder(recordingMode)
            ? new SharpLink.LoadTestBase.LatencyHistogram()
            : null;
        SharpLink.LoadTestBase.LatencyHistogram? realtimeRef = LatencyRecordingPolicy.StartsRealtimeReporter(recordingMode)
            ? new SharpLink.LoadTestBase.LatencyHistogram(200_000)
            : null;
        var lifecycle = new MeasurementStageLifecycle(concurrency);
        var tailObserverRecorder = options.TailObserver && !isWarmup
            ? new StageLatencyRecorder(1, Math.Min(options.MaximumRecordedOperations, 1_000_000))
            : null;
        var failures = new FailureRecorder();
        long realtimeSuccess = 0;
        var workers = new Task<WorkerStageOutcome>[concurrency];
        using var reporterStop = new CancellationTokenSource();

        Task? realtimeReporter = null;
        if (LatencyRecordingPolicy.StartsRealtimeReporter(recordingMode))
        {
            realtimeReporter = Task.Run(async () =>
            {
                var lastUpdate = Stopwatch.GetTimestamp();
                while (!reporterStop.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), reporterStop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    var now = Stopwatch.GetTimestamp();
                    var windowSeconds = Math.Max(0.001, Stopwatch.GetElapsedTime(lastUpdate, now).TotalSeconds);
                    lastUpdate = now;
                    var windowSuccess = Interlocked.Exchange(ref realtimeSuccess, 0);
                    var windowHistogram = Interlocked.Exchange(
                        ref realtimeRef,
                        new SharpLink.LoadTestBase.LatencyHistogram(200_000))!;

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

        Task<TailObserverOutcome>? tailObserverTask = null;
        TaskCompletionSource? tailObserverReady = null;
        if (tailObserverRecorder is not null)
        {
            var observer = tailObserverRecorder.GetWorker(0);
            var observerReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tailObserverReady = observerReady;
            tailObserverTask = Task.Run(async () =>
            {
                long failure = 0;
                observerReady.TrySetResult();
                await lifecycle.WaitForStartAsync().ConfigureAwait(false);
                while (lifecycle.CanStartOperation)
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        var value = await tailObserverRpc!.AddAsync(7, 9).ConfigureAwait(false);
                        if (value != 16)
                            throw new InvalidOperationException($"Tail observer received {value}, expected 16.");
                        observer.RecordTicks(0, Stopwatch.GetTimestamp() - started);
                    }
                    catch (LatencySampleCapacityExceededException)
                    {
                        throw;
                    }
                    catch
                    {
                        failure++;
                    }
                }

                return new TailObserverOutcome(observer.Count, failure);
            }, CancellationToken.None);
        }

        for (var i = 0; i < workers.Length; i++)
        {
            var workerIndex = i;
            var workerRecorder = formalRecorder?.GetWorker(workerIndex);
            // StringCodec writes UTF-16 bytes; keep the requested business payload size exact.
            var echoPayload = operation == "echo"
                ? CreateEchoPayload(options.PayloadSize, options.PayloadPattern, workerIndex)
                : string.Empty;
            workers[i] = Task.Run(async () =>
            {
                long success = 0;
                long failure = 0;
                long sendQueueBackpressureRetries = 0;
                long operationsStarted = 0;
                await lifecycle.ReadyAndWaitForStartAsync(workerIndex).ConfigureAwait(false);

                while (lifecycle.CanStartOperation)
                {
                    operationsStarted++;
                    var start = workerRecorder is not null || diagnosticHistogram is not null
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    while (true)
                    {
                        try
                        {
                            if (operation == "echo")
                            {
                                _ = await rpc.EchoAsync(echoPayload);
                            }
                            else if (operation == "empty")
                            {
                                await rpc.PingAsync();
                            }
                            else if (operation == "yield")
                            {
                                _ = await rpc.YieldAsync(7, 9);
                            }
                            else if (operation == "delay")
                            {
                                _ = await rpc.DelayAsync(7, 9);
                            }
                            else if (operation == "oneway")
                            {
                                await rpc.NotifyAsync(7, 9);
                            }
                            else
                            {
                                _ = await rpc.AddAsync(7, 9);
                            }

                            if (workerRecorder is not null)
                            {
                                var elapsedTicks = Stopwatch.GetTimestamp() - start;
                                workerRecorder.RecordTicks(workerIndex, elapsedTicks);
                                if (diagnosticHistogram is not null)
                                    diagnosticHistogram.Record(formalRecorder!.TicksToMicroseconds(elapsedTicks));
                            }
                            else if (diagnosticHistogram is not null)
                            {
                                var elapsedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                                diagnosticHistogram.Record(elapsedUs);
                                Volatile.Read(ref realtimeRef)!.Record(elapsedUs);
                            }

                            success++;
                            if (recordingMode == LatencyRecordingMode.Diagnostic)
                                Interlocked.Increment(ref realtimeSuccess);
                            break;
                        }
                        catch (LatencySampleCapacityExceededException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (ShouldRetryOneWaySendQueueBackpressure(
                                    retryOneWaySendQueueBackpressure,
                                    operation,
                                    ex))
                            {
                                sendQueueBackpressureRetries++;
                                if (!lifecycle.CanStartOperation)
                                {
                                    failures.Record(ex);
                                    failure++;
                                    break;
                                }

                                await Task.Yield();
                                continue;
                            }

                            failures.Record(ex);
                            failure++;
                            if (ShouldYieldAfterBackpressure(operation, ex))
                                await Task.Yield();
                            break;
                        }
                    }
                }

                return new WorkerStageOutcome(
                    success,
                    failure,
                    sendQueueBackpressureRetries,
                    operationsStarted);
            }, CancellationToken.None);
        }

        var workersTask = Task.WhenAll(workers);
        Task allActivityTask = tailObserverTask is null
            ? workersTask
            : Task.WhenAll(workersTask, tailObserverTask);
        await lifecycle.AllWorkersReady.ConfigureAwait(false);
        if (tailObserverReady is not null)
            await tailObserverReady.Task.ConfigureAwait(false);
        var evidenceBefore = s_evidenceCollector!.Capture();
        var measurementStarted = lifecycle.StartMeasurement();
        var measurementDelay = Task.Delay(TimeSpan.FromSeconds(durationSeconds), CancellationToken.None);
        var firstWorkerFinished = Task.WhenAny(workers);
        var boundary = await Task.WhenAny(measurementDelay, firstWorkerFinished).ConfigureAwait(false);
        var measurementStopped = lifecycle.StopStartingNewOperations();
        var drainTask = lifecycle.WaitForDrainAsync(
            allActivityTask,
            TimeSpan.FromSeconds(options.DrainTimeoutSeconds));
        reporterStop.Cancel();
        if (realtimeReporter is not null)
            await realtimeReporter.ConfigureAwait(false);

        double drainSeconds;
        try
        {
            drainSeconds = await drainTask.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Load test drain exceeded {options.DrainTimeoutSeconds}s; the run is invalid because in-flight RPCs did not complete.");
        }

        if (boundary == firstWorkerFinished)
        {
            var first = await firstWorkerFinished.ConfigureAwait(false);
            await first.ConfigureAwait(false);
            throw new InvalidOperationException("A load-test worker exited before the measurement boundary.");
        }

        var outcomes = await workersTask.ConfigureAwait(false);
        long success = 0;
        long failure = 0;
        long sendQueueBackpressureRetries = 0;
        long operationsStarted = 0;
        foreach (var outcome in outcomes)
        {
            success = checked(success + outcome.Success);
            failure = checked(failure + outcome.Failure);
            sendQueueBackpressureRetries = checked(
                sendQueueBackpressureRetries + outcome.SendQueueBackpressureRetries);
            operationsStarted = checked(operationsStarted + outcome.OperationsStarted);
        }

        var measurementSeconds = Math.Max(
            0.001,
            Stopwatch.GetElapsedTime(measurementStarted, measurementStopped).TotalSeconds);
        var qps = LatencyRecordingPolicy.CalculateThroughput(success, measurementSeconds);
        var oneWayPayloadMegabytesPerSecond = operation == "echo"
            ? qps * options.PayloadSize / (1024d * 1024d)
            : 0;
        var roundTripPayloadMegabytesPerSecond = oneWayPayloadMegabytesPerSecond * 2;
        var total = success + failure;
        var errorRate = total == 0 ? 0 : failure * 100.0 / total;
        var evidence = PerformanceEvidenceCollector.Delta(
            evidenceBefore,
            s_evidenceCollector.Capture());
        LatencyStatistics? formalStatistics = formalRecorder?.Complete();
        var tailObserverOutcome = tailObserverTask is null
            ? TailObserverOutcome.Empty
            : await tailObserverTask.ConfigureAwait(false);
        LatencyStatistics? tailObserverStatistics = tailObserverRecorder?.Complete();
        if (recordingMode == LatencyRecordingMode.ValidationDual)
            LatencyRecorderValidation.ValidateAgainstLegacy(
                formalStatistics!.Value,
                diagnosticHistogram!);
        var result = new StageResult(
            operation,
            concurrency,
            success,
            failure,
            sendQueueBackpressureRetries,
            qps,
            oneWayPayloadMegabytesPerSecond,
            roundTripPayloadMegabytesPerSecond,
            formalStatistics?.P50Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(50)),
            formalStatistics?.P95Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(95)),
            formalStatistics?.P99Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(99)),
            formalStatistics?.P999Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(99.9)),
            formalStatistics?.AverageUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Average),
            formalStatistics?.MinUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Min),
            formalStatistics?.MaxUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Max),
            options.WarmupSeconds,
            measurementSeconds,
            drainSeconds,
            operationsStarted,
            success + failure,
            formalStatistics?.Count ?? diagnosticHistogram?.Count ?? 0,
            formalRecorder?.MaximumTotalSamples ?? 0,
            recordingMode.ToString().ToLowerInvariant(),
            recordingMode switch
            {
                LatencyRecordingMode.Formal => StageLatencyRecorder.Version,
                LatencyRecordingMode.Off => "off-v1",
                LatencyRecordingMode.Diagnostic => "legacy-diagnostic-v1",
                _ => "validation-dual-v1"
            },
            Stopwatch.Frequency,
            LatencyRecordingPolicy.IsFormalComparable(recordingMode),
            tailObserverStatistics?.Count ?? 0,
            tailObserverOutcome.Failure,
            tailObserverStatistics?.P99Us,
            tailObserverStatistics?.P999Us,
            errorRate,
            failures.Top(3),
            evidence);

        if (!isWarmup)
            metrics.UpdateStage(result);

        return result;
    }

    private static string FormatLatency(double? microseconds)
        => microseconds.HasValue
            ? $"{microseconds.Value.ToString("F2", CultureInfo.InvariantCulture)}us"
            : "n/a";

    internal static bool ShouldYieldAfterBackpressure(string operation, Exception exception)
        => operation == "oneway" &&
           exception is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted };

    internal static bool ShouldRetryOneWaySendQueueBackpressure(
        bool retryEnabled,
        string operation,
        Exception exception)
        => retryEnabled &&
           operation == "oneway" &&
           exception is SharpLinkException
           {
               Code: SharpLinkErrorCode.ResourceExhausted,
               Message: var message
           } &&
           message.Contains("(send_queue_capacity)", StringComparison.Ordinal);

    private static SharpLinkServerBuilder ConfigureServer(
        SharpLinkServerBuilder builder,
        LoadTestOptions options)
    {
        builder.ReplaceService<ILoadTestService>(new LoadTestService());
        return options.AdmissionMode switch
        {
            "disabled" => builder,
            "immediate" => builder.UseAdmissionControl(admission =>
                admission.Global.UseConcurrency(4096)),
            "queue" => builder.UseAdmissionControl(admission =>
            {
                admission.Global.UseConcurrency(1);
                admission.MaxQueuedCalls = 4096;
                admission.MaxQueuedBytes = 64L * 1024 * 1024;
                admission.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }),
            "reject" => builder.UseAdmissionControl(admission =>
                admission.Global.UseConcurrency(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(options.AdmissionMode))
        };
    }

    internal static void ConfigureRuntime(
        SharpLinkRuntimeOptions runtime,
        LoadTestOptions options)
    {
        if (options.MaxSendQueueBytes is { } maxSendQueueBytes)
            runtime.FlowControl.MaxSendQueueBytes = maxSendQueueBytes;
        runtime.FlowControl.MaxConcurrentCallsPerConnection = options.MaxConcurrentCallsPerConnection;
        runtime.FlowControl.MaxConcurrentCallsPerServer = options.MaxConcurrentCallsPerServer;
        runtime.Protocol.MaxPendingRequestsPerConnection = options.MaxPendingRequestsPerConnection;
        runtime.Compression.MinimumPayloadBytes = options.CompressionMinimumPayloadBytes;
        runtime.Compression.MinimumSavingsBytes = options.CompressionMinimumSavingsBytes;
        runtime.Compression.MinimumSavingsRatio = options.CompressionMinimumSavingsRatio;
        if (options.CompressionAlgorithm == "none")
            return;
        var level = options.CompressionLevel switch
        {
            "fastest" => CompressionLevel.Fastest,
            "optimal" => CompressionLevel.Optimal,
            "smallest" => CompressionLevel.SmallestSize,
            "nocompression" => CompressionLevel.NoCompression,
            _ => throw new ArgumentOutOfRangeException(nameof(options.CompressionLevel))
        };
        runtime.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli(level));
    }

    private static string CreateEchoPayload(int payloadSize, string pattern, int worker)
    {
        var length = payloadSize / sizeof(char);
        if (pattern == "compressible")
            return new string('x', length);
        var chars = new char[length];
        var random = new Random(42 + worker);
        for (var index = 0; index < chars.Length; index++)
            chars[index] = (char)random.Next(0x0100, 0xd7ff);
        return new string(chars);
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
    public string CompressionAlgorithm { get; private init; } = "none";
    public string CompressionLevel { get; private init; } = "fastest";
    public int CompressionMinimumPayloadBytes { get; private init; } = 1024;
    public int CompressionMinimumSavingsBytes { get; private init; } = 64;
    public double CompressionMinimumSavingsRatio { get; private init; } = 0.05;
    public int? MaxSendQueueBytes { get; private init; }
    public string PayloadPattern { get; private init; } = "compressible";
    public string? JsonOutputPath { get; private init; }
    public LatencyRecordingMode RecordingMode { get; private init; } = LatencyRecordingMode.Formal;
    public int MaximumRecordedOperations { get; private init; } = 30_000_000;
    public int DrainTimeoutSeconds { get; private init; } = 5;
    public bool TailObserver { get; private init; }
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
        var compressionAlgorithm = map.GetValueOrDefault("compression", "none").ToLowerInvariant();
        if (compressionAlgorithm is not ("none" or "brotli"))
            throw new ArgumentException($"Unsupported compression algorithm: {compressionAlgorithm}.");
        var compressionLevel = map.GetValueOrDefault("compression-level", "fastest").ToLowerInvariant();
        if (compressionLevel is not ("fastest" or "optimal" or "smallest" or "nocompression"))
            throw new ArgumentException($"Unsupported compression level: {compressionLevel}.");
        var compressionMinimumPayloadBytes = int.Parse(
            map.GetValueOrDefault("compression-min-payload", "1024"),
            CultureInfo.InvariantCulture);
        var compressionMinimumSavingsBytes = int.Parse(
            map.GetValueOrDefault("compression-min-savings-bytes", "64"),
            CultureInfo.InvariantCulture);
        var compressionMinimumSavingsRatio = double.Parse(
            map.GetValueOrDefault("compression-min-savings-ratio", "0.05"),
            CultureInfo.InvariantCulture);
        var compressionValidation = new SharpLinkCompressionOptions
        {
            MinimumPayloadBytes = compressionMinimumPayloadBytes,
            MinimumSavingsBytes = compressionMinimumSavingsBytes,
            MinimumSavingsRatio = compressionMinimumSavingsRatio
        };
        compressionValidation.Validate();
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
            CompressionAlgorithm = compressionAlgorithm,
            CompressionLevel = compressionLevel,
            CompressionMinimumPayloadBytes = compressionMinimumPayloadBytes,
            CompressionMinimumSavingsBytes = compressionMinimumSavingsBytes,
            CompressionMinimumSavingsRatio = compressionMinimumSavingsRatio,
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

public sealed record StageResult(
    string Operation,
    int Concurrency,
    long Success,
    long Failure,
    long SendQueueBackpressureRetries,
    double Qps,
    double OneWayPayloadMegabytesPerSecond,
    double RoundTripPayloadMegabytesPerSecond,
    double? P50Us,
    double? P95Us,
    double? P99Us,
    double? P999Us,
    double? AvgUs,
    double? MinUs,
    double? MaxUs,
    double WarmupDurationSeconds,
    double MeasurementDurationSeconds,
    double DrainDurationSeconds,
    long OperationsStartedDuringMeasurement,
    long OperationsCompleted,
    long SampleCount,
    int MaximumSampleCapacity,
    string RecorderMode,
    string RecorderVersion,
    long StopwatchFrequency,
    bool FormalComparable,
    long TailObserverSampleCount,
    long TailObserverFailure,
    double? TailObserverP99Us,
    double? TailObserverP999Us,
    double ErrorRatePercent,
    string TopFailures,
    PerformanceStageEvidence Evidence)
{
    public int WorkerCount => Concurrency;
}

public sealed record RealtimeResult(
    string Operation,
    int Concurrency,
    double Qps,
    double P50Us,
    double P95Us,
    double P99Us,
    double P999Us);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerformanceReport<LoadTestOptions, StageResult>))]
[JsonSerializable(typeof(PerformanceReport<LoadTestOptions, HoldCapacityResult>))]
internal sealed partial class LoadTestJsonContext : JsonSerializerContext;

internal readonly record struct WorkerStageOutcome(
    long Success,
    long Failure,
    long SendQueueBackpressureRetries,
    long OperationsStarted);

internal readonly record struct TailObserverOutcome(long SampleCount, long Failure)
{
    public static TailObserverOutcome Empty { get; } = new(0, 0);
}

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

[RpcContract]
public interface ILoadTestService : IService
{
    [NonCancellable]
    ValueTask PingAsync();
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<string> EchoAsync(string value);
    [NonCancellable]
    ValueTask<int> YieldAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> DelayAsync(int left, int right);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> ResetHoldProbeAsync();
    [NonCancellable]
    ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds);
    [NonCancellable]
    ValueTask<int> GetHoldActiveCallsAsync();
    [NonCancellable]
    ValueTask<int> GetHoldPeakActiveCallsAsync();
    [NonCancellable]
    ValueTask<string> GetSessionIdAsync();
}

[RpcService]
public class LoadTestService : ILoadTestService
{
    private readonly HoldCapacityProbe _holdProbe = new();

    public ValueTask PingAsync() => ValueTask.CompletedTask;
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

    public ValueTask NotifyAsync(int left, int right) => ValueTask.CompletedTask;

    public ValueTask<int> ResetHoldProbeAsync() => ValueTask.FromResult(_holdProbe.Reset());

    public ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds)
        => _holdProbe.HoldAsync(generation, expectedAcceptedCalls, holdDurationMilliseconds);

    public ValueTask<int> GetHoldActiveCallsAsync()
        => ValueTask.FromResult(_holdProbe.ActiveCalls);

    public ValueTask<int> GetHoldPeakActiveCallsAsync()
        => ValueTask.FromResult(_holdProbe.PeakActiveCalls);

    public ValueTask<string> GetSessionIdAsync()
        => ValueTask.FromResult(
            SharpLinkCallContext.Current?.SessionId ??
            throw new InvalidOperationException("The current RPC call has no server session identity."));
}
