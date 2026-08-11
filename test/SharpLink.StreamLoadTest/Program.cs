using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.LoadTestBase;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.StreamLoadTest;

public static class Program
{
    private static PerformanceEvidenceCollector? s_evidenceCollector;

    public static async Task Main(string[] args)
    {
        if (args.Any(static x => x is "--help" or "-h"))
        {
            PrintHelp();
            return;
        }

        var options = StreamLoadOptions.Parse(args);
        using var evidenceCollector = new PerformanceEvidenceCollector(options.DetailedSharedMemoryEvidence);
        s_evidenceCollector = evidenceCollector;
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
        Console.WriteLine("  --transport tcp|uds|namedpipe|anonymous|sharedmemory");
        Console.WriteLine("  --host 127.0.0.1 --bind-ip 0.0.0.0 --port 19150");
        Console.WriteLine("  --duration 20 --warmup 5 --concurrency 1,2,4,8,16");
        Console.WriteLine("  --operation all|unary|c2s|s2c|duplex|duplex-equivalent");
        Console.WriteLine("  --stream-size 256");
        Console.WriteLine("  --message-bytes 4096 --messages-per-stream 8 (duplex-equivalent)");
        Console.WriteLine("  --consumer-delay-ms 0 --early-break-after 0 --pause-after 0 --pause-ms 0");
        Console.WriteLine("  --min-connections 1 --max-connections 1");
        Console.WriteLine("  --profile balanced|lowlatency|throughput");
        Console.WriteLine("  --max-send-queue-bytes 67108864 (optional bounded throughput-test override)");
        Console.WriteLine("  --shm-name sharplink-stream-loadtest --shm-capacity 8388608 --shm-spin-count 8");
        Console.WriteLine("  --detailed-shm-evidence (diagnostic counters; do not use for formal timing)");
        Console.WriteLine("  --recording off|formal|diagnostic|validation-dual");
        Console.WriteLine("  --maximum-recorded-operations 30000000 --drain-timeout 30");
        Console.WriteLine("  --json-output artifacts/perf/stream.json");
        Console.WriteLine("  --heartbeat-interval 10 --heartbeat-check-interval 10 --heartbeat-timeout 120");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --transport tcp --operation all --concurrency 1,4,16 --stream-size 512");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --transport namedpipe --operation duplex");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode local --transport sharedmemory --operation duplex");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode server --transport tcp --port 19150");
        Console.WriteLine("  dotnet run --project test/SharpLink.StreamLoadTest -- --mode client --transport tcp --host 127.0.0.1 --port 19150 --operation duplex");
    }

    private static void PrintConfig(StreamLoadOptions options)
    {
        Console.WriteLine($"[Config] mode={options.Mode} transport={options.Transport} op={options.Operation} duration={options.DurationSeconds}s warmup={options.WarmupSeconds}s streamSize={options.StreamSize}");
        if (options.Operation == "duplex-equivalent")
            Console.WriteLine($"[Config] equivalentDuplex={options.MessageBytes}B x {options.MessagesPerStream} messages/stream with full response validation");
        Console.WriteLine(
            $"[Config] concurrency=[{string.Join(',', options.ConcurrencyConfig)}] " +
            $"pool={options.MinConnections}/{options.MaxConnections} profile={options.PerformanceProfile} " +
            $"sendQueue={options.MaxSendQueueBytes?.ToString() ?? "profile-default"}B " +
            $"delay={options.ConsumerDelayMilliseconds}ms earlyBreak={options.EarlyBreakAfter} " +
            $"pause={options.PauseAfter}/{options.PauseMilliseconds}ms " +
            $"recording={options.RecordingMode} sampleCapacity={options.MaximumRecordedOperations} " +
            $"drainTimeout={options.DrainTimeoutSeconds}s");

        if (options.Transport == TransportMode.Tcp)
            Console.WriteLine($"[Config] tcp://{options.Host}:{options.Port} (bind={options.BindIp})");
        else if (options.Transport == TransportMode.Uds)
            Console.WriteLine($"[Config] uds://{options.UdsPath}");
        else if (options.Transport == TransportMode.NamedPipe)
            Console.WriteLine($"[Config] pipe://{options.PipeName}");
        else if (options.Transport == TransportMode.SharedMemory)
            Console.WriteLine($"[Config] shm://{options.SharedMemoryName} capacity={options.SharedMemoryCapacity?.ToString() ?? "profile"} spin={options.SharedMemorySpinCount?.ToString() ?? "profile"}");
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
            options.MinConnections,
            options.MaxConnections,
            static builder => builder,
            options.PerformanceProfile,
            sharedMemoryName: options.SharedMemoryName,
            sharedMemoryCapacity: options.SharedMemoryCapacity,
            sharedMemorySpinCount: options.SharedMemorySpinCount,
            configureServerRuntime: runtime => ConfigureRuntime(runtime, options),
            configureClientRuntime: runtime => ConfigureRuntime(runtime, options));

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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ServerFailure] {exception}");
            throw;
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
            static builder => builder,
            options.PerformanceProfile,
            options.SharedMemoryName,
            options.SharedMemoryCapacity,
            options.SharedMemorySpinCount,
            runtime => ConfigureRuntime(runtime, options));

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
            options.HeartbeatTimeoutSeconds,
            options.MinConnections,
            options.MaxConnections,
            options.PerformanceProfile,
            sharedMemoryName: options.SharedMemoryName,
            sharedMemoryCapacity: options.SharedMemoryCapacity,
            sharedMemorySpinCount: options.SharedMemorySpinCount,
            configureRuntime: runtime => ConfigureRuntime(runtime, options));

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
        var results = new List<StageResult>();
        foreach (var operation in ResolveOperations(options.Operation))
        {
            foreach (var concurrency in options.ConcurrencyConfig)
            {
                var warmupDurationSeconds = 0d;
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"[Warmup] op={operation} c={concurrency} for {options.WarmupSeconds}s");
                    var warmupStarted = Stopwatch.GetTimestamp();
                    _ = await ExecuteStageAsync(
                        rpc,
                        operation,
                        options,
                        options.WarmupSeconds,
                        0,
                        concurrency,
                        isWarmup: true);
                    warmupDurationSeconds = Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds;
                }

                var result = await ExecuteStageAsync(
                    rpc,
                    operation,
                    options,
                    options.DurationSeconds,
                    warmupDurationSeconds,
                    concurrency,
                    isWarmup: false);
                Console.WriteLine($"[Result] op={result.Operation} c={result.Concurrency} qps={result.Qps:F2} ok={result.Success} fail={result.Failure} validationFail={result.ValidationFailure} cancelled={result.Cancelled} err={result.ErrorRatePercent:F2}% p50={FormatLatency(result.P50Us)} p95={FormatLatency(result.P95Us)} p99={FormatLatency(result.P99Us)} p999={FormatLatency(result.P999Us)} avg={FormatLatency(result.AvgUs)} min={FormatLatency(result.MinUs)} max={FormatLatency(result.MaxUs)} measurement={result.MeasurementDurationSeconds:F2}s drain={result.DrainDurationSeconds:F3}s");
                if (result.ValidatedMessages > 0)
                    Console.WriteLine($"[EquivalentDuplex] messages={result.ValidatedMessages} msgps={result.MessagesPerSecond:F2} directionalMiBps={result.DirectionalBusinessMiBPerSecond:F2}");
                if (!string.IsNullOrEmpty(result.TopFailures))
                    Console.WriteLine($"[Failures] {result.TopFailures}");
                results.Add(result);
            }
        }

        PerformanceReportWriter.Write(
            options.JsonOutputPath,
            "SharpLink.StreamLoadTest",
            options,
            results,
            StreamLoadTestJsonContext.Default);
    }

    private static async Task<StageResult> ExecuteStageAsync(
        IStreamLoadService rpc,
        string operation,
        StreamLoadOptions options,
        int durationSeconds,
        double warmupDurationSeconds,
        int concurrency,
        bool isWarmup)
    {
        var recordingMode = isWarmup ? LatencyRecordingMode.Off : options.RecordingMode;
        var formalRecorder = LatencyRecordingPolicy.CreatesFormalRecorder(recordingMode)
            ? new StageLatencyRecorder(concurrency, options.MaximumRecordedOperations)
            : null;
        var diagnosticHistogram = LatencyRecordingPolicy.CreatesDiagnosticRecorder(recordingMode)
            ? new LatencyHistogram()
            : null;
        var lifecycle = new MeasurementStageLifecycle(concurrency);
        var failures = new FailureRecorder();
        var payload = Enumerable.Range(1, options.StreamSize).ToArray();
        var equivalentMessages = operation == "duplex-equivalent"
            ? EquivalentDuplexWorkload.CreateMessages(options.MessageBytes, options.MessagesPerStream)
            : null;

        var workers = new Task<StreamWorkerOutcome>[concurrency];

        for (var i = 0; i < concurrency; i++)
        {
            var workerIndex = i;
            var workerRecorder = formalRecorder?.GetWorker(workerIndex);
            workers[i] = Task.Run(async () =>
            {
                long success = 0;
                long failure = 0;
                long validationFailure = 0;
                long cancelled = 0;
                long validatedMessages = 0;
                long operationsStarted = 0;
                long workerOperationId = 0;
                await lifecycle.ReadyAndWaitForStartAsync(workerIndex).ConfigureAwait(false);
                while (lifecycle.CanStartOperation)
                {
                    operationsStarted++;
                    var start = workerRecorder is not null || diagnosticHistogram is not null
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    try
                    {
                        var operationId = ((long)workerIndex << 48) | ++workerOperationId;
                        var messages = await InvokeOperationAsync(
                            rpc,
                            operation,
                            operationId,
                            payload,
                            equivalentMessages,
                            options,
                            CancellationToken.None);
                        if (workerRecorder is not null)
                        {
                            var elapsedTicks = Stopwatch.GetTimestamp() - start;
                            workerRecorder.RecordTicks(workerIndex, elapsedTicks);
                            if (diagnosticHistogram is not null)
                                diagnosticHistogram.Record(formalRecorder!.TicksToMicroseconds(elapsedTicks));
                        }
                        else if (diagnosticHistogram is not null)
                        {
                            diagnosticHistogram.Record(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
                        }
                        validatedMessages += messages;
                        success++;
                    }
                    catch (LatencySampleCapacityExceededException)
                    {
                        throw;
                    }
                    catch (EquivalentDuplexValidationException ex)
                    {
                        failures.Record(ex);
                        validationFailure++;
                        failure++;
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failures.Record(ex);
                        failure++;
                    }
                }

                return new StreamWorkerOutcome(
                    success,
                    failure,
                    validationFailure,
                    cancelled,
                    validatedMessages,
                    operationsStarted);
            }, CancellationToken.None);
        }

        var workersTask = Task.WhenAll(workers);
        await lifecycle.AllWorkersReady.ConfigureAwait(false);
        var evidenceBefore = s_evidenceCollector!.Capture();
        var measurementStarted = lifecycle.StartMeasurement();
        var measurementDelay = Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        var firstWorkerFinished = Task.WhenAny(workers);
        var boundary = await Task.WhenAny(measurementDelay, firstWorkerFinished).ConfigureAwait(false);
        var measurementStopped = lifecycle.StopStartingNewOperations();
        double drainSeconds;
        try
        {
            drainSeconds = await lifecycle.WaitForDrainAsync(
                workersTask,
                TimeSpan.FromSeconds(options.DrainTimeoutSeconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Stream load-test drain exceeded {options.DrainTimeoutSeconds}s; the run is invalid.");
        }

        if (boundary == firstWorkerFinished)
        {
            var first = await firstWorkerFinished.ConfigureAwait(false);
            await first.ConfigureAwait(false);
            throw new InvalidOperationException("A stream load-test worker exited before the measurement boundary.");
        }

        long success = 0;
        long failure = 0;
        long validationFailure = 0;
        long cancelled = 0;
        long validatedMessages = 0;
        long operationsStarted = 0;
        foreach (var outcome in await workersTask.ConfigureAwait(false))
        {
            success = checked(success + outcome.Success);
            failure = checked(failure + outcome.Failure);
            validationFailure = checked(validationFailure + outcome.ValidationFailure);
            cancelled = checked(cancelled + outcome.Cancelled);
            validatedMessages = checked(validatedMessages + outcome.ValidatedMessages);
            operationsStarted = checked(operationsStarted + outcome.OperationsStarted);
        }

        var elapsed = Math.Max(
            0.001,
            Stopwatch.GetElapsedTime(measurementStarted, measurementStopped).TotalSeconds);
        var total = success + failure;
        var errRate = total == 0 ? 0 : failure * 100.0 / total;
        var equivalentRates = EquivalentDuplexRates.Calculate(
            success,
            failure,
            validatedMessages,
            elapsed,
            options.MessageBytes);
        var evidence = PerformanceEvidenceCollector.Delta(
            evidenceBefore,
            s_evidenceCollector.Capture());
        var formalStatistics = formalRecorder?.Complete();
        if (recordingMode == LatencyRecordingMode.ValidationDual)
            LatencyRecorderValidation.ValidateAgainstLegacy(
                formalStatistics!.Value,
                diagnosticHistogram!);
        return new StageResult(
            operation,
            concurrency,
            success,
            failure,
            validationFailure,
            cancelled,
            LatencyRecordingPolicy.CalculateThroughput(success, elapsed),
            validatedMessages,
            equivalentRates.MessagesPerSecond,
            equivalentRates.DirectionalBusinessMiBPerSecond,
            formalStatistics?.P50Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(50)),
            formalStatistics?.P95Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(95)),
            formalStatistics?.P99Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(99)),
            formalStatistics?.P999Us ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Percentile(99.9)),
            formalStatistics?.AverageUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Average),
            formalStatistics?.MinUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Min),
            formalStatistics?.MaxUs ?? (diagnosticHistogram is null ? null : diagnosticHistogram.Max),
            warmupDurationSeconds,
            elapsed,
            drainSeconds,
            operationsStarted,
            success + failure + cancelled,
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
            errRate,
            failures.Top(3),
            evidence);
    }

    private static string FormatLatency(double? microseconds)
        => microseconds.HasValue ? $"{microseconds.Value:F2}us" : "n/a";

    private static async Task<int> InvokeOperationAsync(
        IStreamLoadService rpc,
        string operation,
        long operationId,
        int[] payload,
        byte[][]? equivalentMessages,
        StreamLoadOptions options,
        CancellationToken ct)
    {
        switch (operation)
        {
            case "unary":
                _ = await rpc.AddAsync(7, 9);
                return 0;
            case "c2s":
                _ = await rpc.UploadAsync(ToStream(payload, ct));
                return 0;
            case "s2c":
                await DrainAsync(rpc.DownloadAsync(payload.Length), options, ct);
                return 0;
            case "duplex":
                await DrainAsync(rpc.DuplexAsync(ToStream(payload, ct)), options, ct);
                return 0;
            case "duplex-equivalent":
                return await EquivalentDuplexWorkload.ExecuteValidatedAsync(
                    rpc,
                    operationId,
                    equivalentMessages!,
                    ct);
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation");
        }
    }

    private static async Task DrainAsync(
        IAsyncEnumerable<int> stream,
        StreamLoadOptions options,
        CancellationToken ct)
    {
        var consumed = 0;
        await foreach (var item in stream.WithCancellation(ct))
        {
            _ = item;
            consumed++;
            if (options.ConsumerDelayMilliseconds > 0)
                await Task.Delay(options.ConsumerDelayMilliseconds, ct).ConfigureAwait(false);
            if (options.PauseAfter > 0 && consumed == options.PauseAfter && options.PauseMilliseconds > 0)
                await Task.Delay(options.PauseMilliseconds, ct).ConfigureAwait(false);
            if (options.EarlyBreakAfter > 0 && consumed >= options.EarlyBreakAfter)
                break;
        }
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

    private static void ConfigureRuntime(
        SharpLinkRuntimeOptions runtime,
        StreamLoadOptions options)
    {
        if (options.MaxSendQueueBytes is { } maxSendQueueBytes)
            runtime.FlowControl.MaxSendQueueBytes = maxSendQueueBytes;
    }
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
    public string SharedMemoryName { get; private init; } = TransportDefaults.GetDefaultSharedMemoryName("sharplink-stream-loadtest");
    public int? SharedMemoryCapacity { get; private init; }
    public int? SharedMemorySpinCount { get; private init; }
    public bool DetailedSharedMemoryEvidence { get; private init; }
    public int DurationSeconds { get; private init; } = 20;
    public int WarmupSeconds { get; private init; } = 5;
    public int[] ConcurrencyConfig { get; private init; } = [1, 2, 4, 8, 16];
    public string Operation { get; private init; } = "all";
    public int StreamSize { get; private init; } = 256;
    public int MessageBytes { get; private init; } = EquivalentDuplexWorkload.DefaultMessageBytes;
    public int MessagesPerStream { get; private init; } = EquivalentDuplexWorkload.DefaultMessagesPerStream;
    public int HeartbeatIntervalSeconds { get; private init; } = 10;
    public int HeartbeatCheckIntervalSeconds { get; private init; } = 10;
    public int HeartbeatTimeoutSeconds { get; private init; } = 120;
    public int MinConnections { get; private init; } = 1;
    public int MaxConnections { get; private init; } = 1;
    public int ConsumerDelayMilliseconds { get; private init; }
    public int EarlyBreakAfter { get; private init; }
    public int PauseAfter { get; private init; }
    public int PauseMilliseconds { get; private init; }
    public SharpLinkPerformanceProfile PerformanceProfile { get; private init; } = SharpLinkPerformanceProfile.Balanced;
    public int? MaxSendQueueBytes { get; private init; }
    public string? JsonOutputPath { get; private init; }
    public LatencyRecordingMode RecordingMode { get; private init; } = LatencyRecordingMode.Formal;
    public int MaximumRecordedOperations { get; private init; } = 30_000_000;
    public int DrainTimeoutSeconds { get; private init; } = 30;

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
        if (operation is not ("all" or "unary" or "c2s" or "s2c" or "duplex" or "duplex-equivalent"))
            throw new ArgumentException($"Unsupported operation: {operation}. Supported: all, unary, c2s, s2c, duplex, duplex-equivalent.");

        var concurrencyConfig = map.TryGetValue("concurrency", out var concurrencyStr)
            ? concurrencyStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToArray()
            : [1, 2, 4, 8, 16];

        var minConnections = int.Parse(map.GetValueOrDefault("min-connections", "1"));
        var maxConnections = int.Parse(map.GetValueOrDefault("max-connections", "1"));
        new SharpLinkConnectionPoolOptions
        {
            MinConnections = minConnections,
            MaxConnections = maxConnections
        }.Validate();
        if (transport == TransportMode.AnonymousPipe && maxConnections != 1)
            throw new ArgumentException("Anonymous-pipe load tests require --max-connections 1.");
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

        var profileText = map.GetValueOrDefault("profile", "balanced");
        var profile = profileText.ToLowerInvariant() switch
        {
            "balanced" => SharpLinkPerformanceProfile.Balanced,
            "lowlatency" => SharpLinkPerformanceProfile.LowLatency,
            "throughput" => SharpLinkPerformanceProfile.Throughput,
            _ => throw new ArgumentException($"Unsupported performance profile: {profileText}.")
        };
        var maxSendQueueBytes = ParseOptionalInt(map, "max-send-queue-bytes");
        if (maxSendQueueBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSendQueueBytes));
        var messageBytes = int.Parse(map.GetValueOrDefault("message-bytes", EquivalentDuplexWorkload.DefaultMessageBytes.ToString()));
        var messagesPerStream = int.Parse(map.GetValueOrDefault("messages-per-stream", EquivalentDuplexWorkload.DefaultMessagesPerStream.ToString()));
        EquivalentDuplexWorkload.ValidateDimensions(messageBytes, messagesPerStream);
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
            map.GetValueOrDefault("maximum-recorded-operations", "30000000"));
        if (maximumRecordedOperations <= 0 ||
            recordingMode is LatencyRecordingMode.Formal or LatencyRecordingMode.ValidationDual &&
            concurrencyConfig.Any(concurrency => maximumRecordedOperations < concurrency))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecordedOperations));
        }
        var drainTimeoutSeconds = int.Parse(map.GetValueOrDefault("drain-timeout", "30"));
        if (drainTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(drainTimeoutSeconds));

        return new StreamLoadOptions
        {
            Mode = mode,
            Transport = transport,
            Host = map.GetValueOrDefault("host", "127.0.0.1"),
            BindIp = map.GetValueOrDefault("bind-ip", "0.0.0.0"),
            Port = int.Parse(map.GetValueOrDefault("port", "19150")),
            UdsPath = map.GetValueOrDefault("uds-path", TransportDefaults.GetDefaultUdsPath("sl_stream_loadtest")),
            PipeName = map.GetValueOrDefault("pipe-name", TransportDefaults.GetDefaultPipeName("sharplink-stream-loadtest")),
            SharedMemoryName = map.GetValueOrDefault("shm-name", TransportDefaults.GetDefaultSharedMemoryName("sharplink-stream-loadtest")),
            SharedMemoryCapacity = sharedMemoryCapacity,
            SharedMemorySpinCount = sharedMemorySpinCount,
            DetailedSharedMemoryEvidence = map.TryGetValue("detailed-shm-evidence", out var detailedEvidence) &&
                                           bool.Parse(detailedEvidence),
            DurationSeconds = int.Parse(map.GetValueOrDefault("duration", "20")),
            WarmupSeconds = int.Parse(map.GetValueOrDefault("warmup", "5")),
            ConcurrencyConfig = concurrencyConfig.Length == 0 ? [1] : concurrencyConfig,
            Operation = operation,
            StreamSize = int.Parse(map.GetValueOrDefault("stream-size", "256")),
            MessageBytes = messageBytes,
            MessagesPerStream = messagesPerStream,
            HeartbeatIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-interval", "10")),
            HeartbeatCheckIntervalSeconds = int.Parse(map.GetValueOrDefault("heartbeat-check-interval", "10")),
            HeartbeatTimeoutSeconds = int.Parse(map.GetValueOrDefault("heartbeat-timeout", "120")),
            MinConnections = minConnections,
            MaxConnections = maxConnections,
            ConsumerDelayMilliseconds = ParseNonNegative(map, "consumer-delay-ms"),
            EarlyBreakAfter = ParseNonNegative(map, "early-break-after"),
            PauseAfter = ParseNonNegative(map, "pause-after"),
            PauseMilliseconds = ParseNonNegative(map, "pause-ms"),
            PerformanceProfile = profile,
            MaxSendQueueBytes = maxSendQueueBytes,
            JsonOutputPath = map.GetValueOrDefault("json-output"),
            RecordingMode = recordingMode,
            MaximumRecordedOperations = maximumRecordedOperations,
            DrainTimeoutSeconds = drainTimeoutSeconds
        };
    }

    private static int ParseNonNegative(Dictionary<string, string> map, string key)
    {
        var value = int.Parse(map.GetValueOrDefault(key, "0"));
        return value >= 0 ? value : throw new ArgumentOutOfRangeException(key);
    }

    private static int? ParseOptionalInt(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) ? int.Parse(value) : null;
}

public sealed record StageResult(
    string Operation,
    int Concurrency,
    long Success,
    long Failure,
    long ValidationFailure,
    long Cancelled,
    double Qps,
    long ValidatedMessages,
    double MessagesPerSecond,
    double DirectionalBusinessMiBPerSecond,
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
    double ErrorRatePercent,
    string TopFailures,
    PerformanceStageEvidence Evidence)
{
    public int WorkerCount => Concurrency;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerformanceReport<StreamLoadOptions, StageResult>))]
internal sealed partial class StreamLoadTestJsonContext : JsonSerializerContext;

internal readonly record struct StreamWorkerOutcome(
    long Success,
    long Failure,
    long ValidationFailure,
    long Cancelled,
    long ValidatedMessages,
    long OperationsStarted);

[RpcContract]
public interface IStreamLoadService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<long> UploadAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<int> DownloadAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<(long OperationId, byte[] Payload)> DuplexEquivalentAsync(
        long operationId,
        IAsyncEnumerable<byte[]> payloads);
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

    public async IAsyncEnumerable<(long OperationId, byte[] Payload)> DuplexEquivalentAsync(
        long operationId,
        IAsyncEnumerable<byte[]> payloads)
    {
        await foreach (var payload in payloads)
        {
            yield return (operationId, payload);
            await Task.CompletedTask;
        }
    }
}
