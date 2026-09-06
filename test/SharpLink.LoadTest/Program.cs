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
            $"admission={options.AdmissionMode} " +
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
                    .UseTcp(0, IPAddress.Parse(options.BindIp))
                    .AllowUnencrypted()
                    .AllowUnauthenticated();
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
                var warmupDurationSeconds = 0d;
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"[Client] warmup {options.WarmupSeconds}s @ c={concurrency}");
                    var warmupStarted = Stopwatch.GetTimestamp();
                    _ = await ExecuteStageAsync(
                        rpc,
                        tailObserverRpc,
                        options,
                        options.WarmupSeconds,
                        0,
                        concurrency,
                        metrics,
                        retryOneWaySendQueueBackpressure,
                        isWarmup: true);
                    warmupDurationSeconds = Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds;
                }

                var result = await ExecuteStageAsync(
                    rpc,
                    tailObserverRpc,
                    options,
                    options.DurationSeconds,
                    warmupDurationSeconds,
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
        double warmupDurationSeconds,
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
        var lifecycle = new MeasurementStageLifecycle(
            concurrency,
            options.TailObserver && !isWarmup ? 1 : 0);
        var tailObserverRecorder = options.TailObserver && !isWarmup
            ? new StageLatencyRecorder(1, options.TailObserverMaximumRecordedOperations)
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
                while (lifecycle.TryBeginOperationStart(concurrency, out var admission))
                {
                    try
                    {
                        long started;
                        ValueTask<int> completion;
                        using (admission)
                        {
                            started = Stopwatch.GetTimestamp();
                            completion = tailObserverRpc!.AddAsync(7, 9);
                        }

                        var value = await completion.ConfigureAwait(false);
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

                while (lifecycle.TryBeginOperationStart(workerIndex, out var admission))
                {
                    operationsStarted++;
                    var start = workerRecorder is not null || diagnosticHistogram is not null
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    while (true)
                    {
                        try
                        {
                            PendingLoadOperation pendingOperation;
                            using (admission)
                                pendingOperation = StartLoadOperation(rpc, operation, echoPayload);
                            switch (pendingOperation.Kind)
                            {
                                case PendingLoadOperationKind.Void:
                                    await pendingOperation.VoidCompletion.ConfigureAwait(false);
                                    break;
                                case PendingLoadOperationKind.Int32:
                                    _ = await pendingOperation.Int32Completion.ConfigureAwait(false);
                                    break;
                                case PendingLoadOperationKind.String:
                                    _ = await pendingOperation.StringCompletion.ConfigureAwait(false);
                                    break;
                                default:
                                    throw new InvalidOperationException("Unknown pending load operation kind.");
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
                                await Task.Yield();
                                if (!lifecycle.TryBeginOperationStart(workerIndex, out admission))
                                {
                                    failures.Record(ex);
                                    failure++;
                                    break;
                                }

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
            warmupDurationSeconds,
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

    private static PendingLoadOperation StartLoadOperation(
        ILoadTestService rpc,
        string operation,
        string echoPayload)
        => operation switch
        {
            "echo" => PendingLoadOperation.From(rpc.EchoAsync(echoPayload)),
            "empty" => PendingLoadOperation.From(rpc.PingAsync()),
            "yield" => PendingLoadOperation.From(rpc.YieldAsync(7, 9)),
            "delay" => PendingLoadOperation.From(rpc.DelayAsync(7, 9)),
            "oneway" => PendingLoadOperation.From(rpc.NotifyAsync(7, 9)),
            _ => PendingLoadOperation.From(rpc.AddAsync(7, 9))
        };

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
