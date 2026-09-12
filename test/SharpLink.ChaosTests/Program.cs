using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ChaosTests;

public static class Program
{
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] OperationNames =
    [
        "Unary",
        "ServerStreamingEarlyBreak",
        "ClientStreaming",
        "Cancellation",
        "OneWay",
        "DuplexStreaming"
    ];
    private const int ConsecutiveRecoveryProbeCount = 5;

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(static argument => argument is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }

        var options = ChaosOptions.Parse(args);
        var runStartedUtc = DateTimeOffset.UtcNow;
        var commit = GetCommit();
        var workingTreeDirty = GetWorkingTreeDirty();
        using var metrics = new ChaosMetricObserver();
        using var duration = new CancellationTokenSource(options.Duration);
        var failures = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var failureSamples = new ConcurrentQueue<string>();
        var memorySamples = new ConcurrentQueue<MemorySample>();
        var unobservedTaskExceptionSamples = new ConcurrentQueue<string>();
        var serverStops = new ConcurrentQueue<ChaosServerStopObservation>();
        var reportGate = new Lock();
        var phase = "Starting";
        var soakStarted = Stopwatch.GetTimestamp();
        var startedMemory = 0L;
        long success = 0;
        var operationAttempts = new long[OperationNames.Length];
        long expectedFailures = 0;
        long unexpectedFailures = 0;
        long unobservedTaskExceptions = 0;
        long faultGeneration = 0;
        long maxRecoveryMilliseconds = 0;
        long reportWriteFailures = 0;
        string? reportWriteFailure = null;
        var restartCount = 0;
        ChaosDiagnosticArtifact? diagnosticArtifact = null;
        Task<ChaosDiagnosticArtifact>? diagnosticCaptureTask = null;
        var diagnosticGate = new Lock();
        using var clientLogs = new ChaosLoggerFactory();
        using var serverLogs = new ChaosLoggerFactory();

        UnhandledExceptionEventHandler unhandledHandler = (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception ??
                            new InvalidOperationException(eventArgs.ExceptionObject?.ToString() ?? "Unknown unhandled failure.");
            TryWriteReport(
                "Failed",
                phase,
                10,
                ChaosFailure.FromException(exception),
                drain: null,
                isFinal: true);
        };
        AppDomain.CurrentDomain.UnhandledException += unhandledHandler;
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedTaskExceptionHandler = (_, eventArgs) =>
        {
            Interlocked.Increment(ref unobservedTaskExceptions);
            if (unobservedTaskExceptionSamples.Count < 20)
                unobservedTaskExceptionSamples.Enqueue(eventArgs.Exception.ToString());
            eventArgs.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += unobservedTaskExceptionHandler;

        phase = "StartingServer";
        var server = await ChaosServer.StartAsync(
            options.Transport,
            options.SharedMemoryName,
            port: 0,
            loggerFactory: serverLogs).ConfigureAwait(false);
        var port = server.Port;
        var clientBuilder = SharpClientBuilder.Create()
            .UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
            .UseRequestTimeout(TimeSpan.FromSeconds(2))
            .UseLoggerFactory(clientLogs)
            .UseConnectionPool(pool =>
            {
                pool.MinConnections = 1;
                pool.MaxConnections = Math.Min(Environment.ProcessorCount, 4);
            });
        if (options.Transport == ChaosTransport.SharedMemory)
            clientBuilder.UseSharedMemory(options.SharedMemoryName);
        else
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        await using var client = clientBuilder.Build();

        phase = "ConnectingClient";
        await client.ConnectAsync(duration.Token).ConfigureAwait(false);
        var service = client.Get<IChaosService>();
        phase = "Warmup";
        await WarmUpAsync(service, duration.Token).ConfigureAwait(false);
        soakStarted = Stopwatch.GetTimestamp();
        var startedSample = CaptureResourceSample(0) with
        {
            UnobservedTaskExceptions = Volatile.Read(ref unobservedTaskExceptions)
        };
        startedMemory = startedSample.RetainedBytes;
        memorySamples.Enqueue(startedSample);
        phase = "Workload";
        TryWriteReport("Running", phase, null, failure: null, drain: null, isFinal: false);
        var memorySampler = SampleRetainedMemoryAsync();
        var workers = new Task[options.Concurrency];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            var workerId = worker;
            workers[worker] = RunWorkerAsync(
                service,
                workerId,
                duration.Token,
                () => Volatile.Read(ref faultGeneration),
                operation => Interlocked.Increment(ref operationAttempts[operation]),
                () => Interlocked.Increment(ref success),
                () => Interlocked.Increment(ref expectedFailures),
                RecordUnexpectedFailure);
        }

        var restarter = RestartLoopAsync();
        await Task.WhenAll(workers).ConfigureAwait(false);
        await restarter.ConfigureAwait(false);
        await memorySampler.ConfigureAwait(false);

        phase = "StoppingClient";
        await client.StopAsync().ConfigureAwait(false);
        phase = "StoppingServer";
        serverStops.Enqueue(await server.StopAsync("FinalStop").ConfigureAwait(false));
        phase = "DrainingMetrics";
        var drain = await metrics.WaitForZeroAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (options.InjectUnobservedTaskException)
            CreateUnobservedTaskExceptionForGateProbe();
        var finalSample = CaptureResourceSample(Stopwatch.GetElapsedTime(soakStarted).TotalSeconds) with
        {
            UnobservedTaskExceptions = Volatile.Read(ref unobservedTaskExceptions)
        };
        var endedMemory = finalSample.RetainedBytes;
        var memoryGrowthPercent = startedMemory == 0
            ? 0
            : (endedMemory - startedMemory) * 100.0 / startedMemory;
        memorySamples.Enqueue(finalSample);
        var orderedMemorySamples = memorySamples.OrderBy(static sample => sample.ElapsedSeconds).ToArray();
        var lastSixHoursGrowthPercent = CalculateWindowGrowth(orderedMemorySamples, TimeSpan.FromHours(6));
        if (options.InjectClientError)
            clientLogs.InjectErrorForGateProbe("client");
        if (options.InjectServerError)
            serverLogs.InjectErrorForGateProbe("server");
        Task<ChaosDiagnosticArtifact>? activeDiagnosticCapture;
        lock (diagnosticGate)
            activeDiagnosticCapture = diagnosticCaptureTask;
        if (activeDiagnosticCapture is not null)
            diagnosticArtifact = await activeDiagnosticCapture.ConfigureAwait(false);
        var exitCode = 0;
        ChaosFailure? terminalFailure = null;
        if (!drain.Drained)
        {
            exitCode = 5;
            terminalFailure = new ChaosFailure(
                nameof(InvalidOperationException),
                drain.Describe(),
                null);
            if (options.DumpOnFailure && diagnosticArtifact is null)
                diagnosticArtifact = await CaptureProcessDumpAsync(options.JsonOutputPath).ConfigureAwait(false);
        }
        else if (unexpectedFailures != 0)
        {
            exitCode = 2;
            terminalFailure = new ChaosFailure(
                "UnexpectedFailures",
                $"Chaos recorded {unexpectedFailures} unexpected failures.",
                null);
        }
        else if (Volatile.Read(ref unobservedTaskExceptions) != 0)
        {
            exitCode = 7;
            terminalFailure = new ChaosFailure(
                "UnobservedTaskExceptions",
                $"Chaos captured {Volatile.Read(ref unobservedTaskExceptions)} unobserved Task exception(s).",
                string.Join(Environment.NewLine, unobservedTaskExceptionSamples));
        }
        else if (clientLogs.ErrorCount != 0)
        {
            exitCode = 2;
            terminalFailure = new ChaosFailure(
                "ClientErrorLogs",
                $"Chaos captured {clientLogs.ErrorCount} client Error log(s).",
                string.Join(Environment.NewLine, clientLogs.AllSnapshot()));
        }
        else if (serverLogs.ErrorCount != 0)
        {
            exitCode = 2;
            terminalFailure = new ChaosFailure(
                "ServerErrorLogs",
                $"Chaos captured {serverLogs.ErrorCount} server Error log(s).",
                string.Join(Environment.NewLine, serverLogs.AllSnapshot()));
        }
        else if (Volatile.Read(ref reportWriteFailures) != 0)
        {
            exitCode = 6;
            terminalFailure = new ChaosFailure(
                "ReportWriteFailure",
                $"Chaos failed to write its requested report {Volatile.Read(ref reportWriteFailures)} time(s).",
                Volatile.Read(ref reportWriteFailure));
        }
        else if (success == 0 || expectedFailures == 0 || restartCount == 0 ||
                 Enumerable.Range(0, operationAttempts.Length)
                     .Any(index => Volatile.Read(ref operationAttempts[index]) == 0))
        {
            exitCode = 3;
            terminalFailure = new ChaosFailure(
                "InsufficientCoverage",
                $"Chaos completed with success={success}, restarts={restartCount}, and operations=" +
                string.Join(",", CreateOperationAttemptSnapshot().Select(static item => $"{item.Key}:{item.Value}")) + ".",
                null);
        }
        else if (lastSixHoursGrowthPercent is > 5)
        {
            exitCode = 4;
            terminalFailure = new ChaosFailure(
                "RetainedMemoryGrowth",
                $"Last-six-hour retained memory growth was {lastSixHoursGrowthPercent.Value:F2}%.",
                null);
        }

        phase = exitCode == 0 ? "Completed" : "FailedGate";
        var finalReportWritten = TryWriteReport(
            exitCode == 0 ? "Passed" : "Failed",
            phase,
            exitCode,
            terminalFailure,
            drain,
            isFinal: true);
        if (!finalReportWritten && exitCode == 0)
        {
            exitCode = 6;
            phase = "FailedGate";
            terminalFailure = new ChaosFailure(
                "ReportWriteFailure",
                "Chaos failed to write its requested final report.",
                Volatile.Read(ref reportWriteFailure));
        }
        AppDomain.CurrentDomain.UnhandledException -= unhandledHandler;
        TaskScheduler.UnobservedTaskException -= unobservedTaskExceptionHandler;

        Console.WriteLine(
            $"CHAOS_RESULT success={success} injected={expectedFailures} unexpected={unexpectedFailures} " +
            $"restarts={restartCount} clientErrors={clientLogs.ErrorCount} serverErrors={serverLogs.ErrorCount} " +
            $"unobserved={Volatile.Read(ref unobservedTaskExceptions)} " +
            $"retained={startedMemory}->{endedMemory} ({memoryGrowthPercent:F2}%)");
        foreach (var error in serverLogs.AllSnapshot())
            Console.WriteLine($"CHAOS_SERVER_ERROR {error}");
        return exitCode;

        async Task RestartLoopAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(options.RestartInterval, duration.Token).ConfigureAwait(false);
                    clientLogs.Clear();
                    serverLogs.Clear();
                    Interlocked.Increment(ref faultGeneration);
                    var recoveryStarted = Stopwatch.GetTimestamp();
                    using var recoveryTimeout = new CancellationTokenSource(RecoveryTimeout);
                    serverStops.Enqueue(await server.StopAsync("RollingRestart").ConfigureAwait(false));
                    try
                    {
                        server = await ChaosServer.StartWithRetryAsync(
                                options.Transport,
                                options.SharedMemoryName,
                                port,
                                serverLogs,
                                recoveryTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (recoveryTimeout.IsCancellationRequested)
                    {
                        RecordUnexpectedFailure(CreateRecoveryTimeoutException(client.State, clientLogs.Snapshot()));
                        await duration.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    Interlocked.Increment(ref restartCount);
                    if (!await WaitForRecoveryAsync(service, recoveryTimeout.Token).ConfigureAwait(false))
                    {
                        RecordUnexpectedFailure(CreateRecoveryTimeoutException(client.State, clientLogs.Snapshot()));
                        await duration.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    var recoveryMilliseconds = (long)Math.Ceiling(
                        Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
                    UpdateMaximum(ref maxRecoveryMilliseconds, recoveryMilliseconds);
                    Interlocked.Increment(ref faultGeneration);
                }
            }
            catch (OperationCanceledException) when (duration.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                RecordUnexpectedFailure(exception);
                await duration.CancelAsync().ConfigureAwait(false);
            }
        }

        void RecordUnexpectedFailure(Exception exception)
        {
            var unexpectedCount = Interlocked.Increment(ref unexpectedFailures);
            var key = DescribeFailure(exception);
            failures.AddOrUpdate(key, 1, static (_, count) => count + 1);
            if (failureSamples.Count < 20)
                failureSamples.Enqueue(exception.ToString());
            if (unexpectedCount != 1)
                return;

            if (options.DumpOnFailure)
            {
                lock (diagnosticGate)
                    diagnosticCaptureTask ??= CaptureProcessDumpAsync(options.JsonOutputPath);
            }
            TryWriteReport(
                "RunningWithFailure",
                phase,
                null,
                ChaosFailure.FromException(exception),
                drain: null,
                isFinal: false);
            if (options.StopOnUnexpectedFailure)
                duration.Cancel();
        }

        async Task SampleRetainedMemoryAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(options.CheckpointInterval, duration.Token).ConfigureAwait(false);
                    var sample = CaptureResourceSample(
                        Stopwatch.GetElapsedTime(soakStarted).TotalSeconds) with
                    {
                        UnobservedTaskExceptions = Volatile.Read(ref unobservedTaskExceptions)
                    };
                    memorySamples.Enqueue(sample);
                    TryWriteReport("Running", phase, null, failure: null, drain: null, isFinal: false);
                    Console.WriteLine(
                        $"CHAOS_CHECKPOINT elapsed={sample.ElapsedSeconds:F0}s success={Volatile.Read(ref success)} " +
                        $"unexpected={Volatile.Read(ref unexpectedFailures)} restarts={Volatile.Read(ref restartCount)} " +
                        $"retained={sample.RetainedBytes} workingSet={sample.ProcessWorkingSetBytes} " +
                        $"private={sample.ProcessPrivateBytes} gcHeap={sample.GcHeapSizeBytes} " +
                        $"gen={sample.Gen0Collections}/{sample.Gen1Collections}/{sample.Gen2Collections} " +
                        $"threads={sample.ProcessThreadCount}/{sample.ThreadPoolThreadCount} " +
                        $"pending={sample.ThreadPoolPendingWorkItemCount} " +
                        $"dispatchers={sample.DispatcherRetainedCount} " +
                        $"unobserved={sample.UnobservedTaskExceptions}");
                }
            }
            catch (OperationCanceledException) when (duration.IsCancellationRequested)
            {
            }
        }

        ChaosReport CreateReport(
            string status,
            string currentPhase,
            int? currentExitCode,
            ChaosFailure? failure,
            ChaosDrainResult? drain,
            bool isFinal)
        {
            var samples = memorySamples.OrderBy(static sample => sample.ElapsedSeconds).ToArray();
            var latestMemory = samples.Length == 0 ? 0 : samples[^1].RetainedBytes;
            var growth = startedMemory == 0
                ? 0
                : (latestMemory - startedMemory) * 100.0 / startedMemory;
            return new ChaosReport(
                DateTimeOffset.UtcNow,
                runStartedUtc,
                status,
                currentPhase,
                currentExitCode,
                isFinal,
                commit,
                workingTreeDirty,
                Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Version.ToString(),
                options.Duration.TotalSeconds,
                Stopwatch.GetElapsedTime(soakStarted).TotalSeconds,
                options.CheckpointInterval.TotalSeconds,
                options.RestartInterval.TotalSeconds,
                options.Concurrency,
                options.Transport.ToString(),
                options.DumpOnFailure,
                options.StopOnUnexpectedFailure,
                Volatile.Read(ref restartCount),
                Volatile.Read(ref success),
                CreateOperationAttemptSnapshot(),
                Volatile.Read(ref expectedFailures),
                Volatile.Read(ref unexpectedFailures),
                Volatile.Read(ref unobservedTaskExceptions),
                Volatile.Read(ref maxRecoveryMilliseconds),
                startedMemory,
                latestMemory,
                growth,
                CalculateWindowGrowth(samples, TimeSpan.FromHours(6)),
                samples,
                metrics.Snapshot(),
                metrics.ActiveCallBreakdownSnapshot(),
                drain,
                failure,
                diagnosticArtifact,
                failures.OrderByDescending(static item => item.Value)
                    .ToDictionary(static item => item.Key, static item => item.Value),
                [.. failureSamples],
                [.. unobservedTaskExceptionSamples],
                clientLogs.AllSnapshot(),
                serverLogs.AllSnapshot(),
                [.. serverStops]);
        }

        bool TryWriteReport(
            string status,
            string currentPhase,
            int? currentExitCode,
            ChaosFailure? failure,
            ChaosDrainResult? drain,
            bool isFinal)
        {
            if (string.IsNullOrWhiteSpace(options.JsonOutputPath))
                return true;
            try
            {
                lock (reportGate)
                {
                    WriteReport(
                        options.JsonOutputPath,
                        CreateReport(status, currentPhase, currentExitCode, failure, drain, isFinal));
                }
                return true;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref reportWriteFailures);
                Interlocked.CompareExchange(ref reportWriteFailure, exception.ToString(), null);
                Console.Error.WriteLine(
                    $"CHAOS_REPORT_WRITE_FAILED type={exception.GetType().FullName} message={exception.Message}");
                return false;
            }
        }

        IReadOnlyDictionary<string, long> CreateOperationAttemptSnapshot()
            => Enumerable.Range(0, OperationNames.Length)
                .ToDictionary(
                    static index => OperationNames[index],
                    index => Volatile.Read(ref operationAttempts[index]),
                    StringComparer.Ordinal);
    }

    private static async Task RunWorkerAsync(
        IChaosService service,
        int workerId,
        CancellationToken runToken,
        Func<long> getFaultGeneration,
        Action<int> attempt,
        Action success,
        Action expectedFailure,
        Action<Exception> unexpectedFailure)
    {
        var iteration = 0;
        while (!runToken.IsCancellationRequested)
        {
            var operation = (workerId + iteration++) % 6;
            attempt(operation);
            var operationGeneration = getFaultGeneration();
            try
            {
                switch (operation)
                {
                    case 0:
                        var value = await service.AddAsync(workerId, iteration).ConfigureAwait(false);
                        if (value != workerId + iteration)
                            throw new InvalidDataException("Unary result was corrupted.");
                        break;
                    case 1:
                        var received = 0;
                        await foreach (var item in service.StreamAsync(32, runToken)
                                           .WithCancellation(runToken).ConfigureAwait(false))
                        {
                            if (item != received)
                                throw new InvalidDataException("Server stream ordering was corrupted.");
                            if (++received == 8)
                                break;
                        }
                        break;
                    case 2:
                        var sum = await service.UploadAsync(CreateValues(runToken)).ConfigureAwait(false);
                        if (sum != 120)
                            throw new InvalidDataException($"Client stream result was corrupted: {sum}/120.");
                        break;
                    case 3:
                        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken))
                        {
                            // A finite server delay makes this assertion depend on whether the
                            // ThreadPool services the cancellation timer before the delay timer.
                            // Under sustained load the shorter timer can legitimately run late.
                            // An infinite server delay can only end through RPC cancellation or
                            // the call deadline, so a successful result is a real contract breach.
                            cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
                            await service.DelayAsync(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
                            throw new InvalidOperationException("Cancellation injection completed successfully.");
                        }
                    case 4:
                        await service.PublishAsync(workerId, iteration).ConfigureAwait(false);
                        break;
                    default:
                        var duplexCount = 0;
                        await foreach (var item in service.DuplexAsync(CreateValues(runToken))
                                           .ConfigureAwait(false))
                        {
                            var expected = duplexCount * 2;
                            if (item != expected)
                            {
                                throw new InvalidDataException(
                                    $"Duplex stream item was corrupted: {item}/{expected}.");
                            }
                            duplexCount++;
                        }
                        if (duplexCount != 16)
                        {
                            throw new InvalidDataException(
                                $"Duplex stream returned only {duplexCount}/16 items.");
                        }
                        break;
                }
                success();
            }
            catch (Exception exception) when (runToken.IsCancellationRequested &&
                                              exception is OperationCanceledException or SharpLinkException)
            {
                break;
            }
            catch (Exception exception) when (IsExpected(
                       operation,
                       exception,
                       operationGeneration,
                       getFaultGeneration()))
            {
                expectedFailure();
                // Fail-fast RPCs can complete synchronously while every connection is
                // unavailable. Pace retries so the load generator does not monopolize
                // the ThreadPool with an exception storm and starve the reconnect timer
                // that this scenario is intended to verify.
                try
                {
                    await Task.Delay(1, runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                unexpectedFailure(exception);
            }
        }
    }

    private static async IAsyncEnumerable<int> CreateValues(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var value = 0; value < 16; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.CompletedTask;
        }
    }

    private static async Task WarmUpAsync(IChaosService service, CancellationToken cancellationToken)
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            _ = await service.AddAsync(iteration, 1).ConfigureAwait(false);
            _ = await service.UploadAsync(CreateValues(cancellationToken)).ConfigureAwait(false);
            await service.PublishAsync(iteration, 1).ConfigureAwait(false);
            await foreach (var _ in service.StreamAsync(8, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
            }
            var duplexCount = 0;
            await foreach (var item in service.DuplexAsync(CreateValues(cancellationToken))
                               .ConfigureAwait(false))
            {
                if (item != duplexCount * 2)
                    throw new InvalidDataException("Duplex warmup result was corrupted.");
                duplexCount++;
            }
            if (duplexCount != 16)
                throw new InvalidDataException("Duplex warmup returned an incomplete stream.");
        }
    }

    private static bool IsExpected(
        int operation,
        Exception exception,
        long operationGeneration,
        long currentGeneration)
    {
        if (operation == 3 && exception is OperationCanceledException)
            return true;
        if (operation == 3 && exception is SharpLinkException
            {
                Code: SharpLinkErrorCode.Cancelled or SharpLinkErrorCode.DeadlineExceeded
            })
        {
            return true;
        }

        if ((operationGeneration & 1L) == 0 && operationGeneration == currentGeneration)
        {
            return false;
        }

        return exception is SocketException or IOException or ObjectDisposedException or
            SharpLinkException
        {
            Code: SharpLinkErrorCode.Unavailable or SharpLinkErrorCode.ConnectionClosed or
                    SharpLinkErrorCode.DeadlineExceeded or SharpLinkErrorCode.Cancelled
        };
    }

    private static async Task<bool> WaitForRecoveryAsync(
        IChaosService service,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)Math.Ceiling(RecoveryTimeout.TotalSeconds * Stopwatch.Frequency);
        var consecutiveSuccesses = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var probe = await service.AddAsync(20, 22).ConfigureAwait(false);
                if (probe != 42)
                    throw new InvalidDataException("Recovery probe result was corrupted.");
                if (++consecutiveSuccesses >= ConsecutiveRecoveryProbeCount)
                    return true;
            }
            catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException or
                                              SharpLinkException
            {
                Code: SharpLinkErrorCode.Unavailable or
                                                      SharpLinkErrorCode.ConnectionClosed or
                                                      SharpLinkErrorCode.DeadlineExceeded or
                                                      SharpLinkErrorCode.Cancelled
            })
            {
                consecutiveSuccesses = 0;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
                return false;
            try
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
        return false;
    }

    private static TimeoutException CreateRecoveryTimeoutException(
        SharpLinkConnectionState state,
        IReadOnlyList<string> clientErrors)
    {
        var diagnostics = clientErrors.Count == 0
            ? "No client error logs were captured during this restart generation."
            : string.Join(" | ", clientErrors);
        return new TimeoutException(
            $"Client did not complete a probe RPC within {RecoveryTimeout.TotalSeconds:F0} " +
            $"seconds of a server restart. State={state}. ClientErrors={diagnostics}");
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static string DescribeFailure(Exception exception)
        => exception is SharpLinkException sharpLink
            ? $"{nameof(SharpLinkException)}[{sharpLink.Code}]"
            : exception.GetType().Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateUnobservedTaskExceptionForGateProbe()
    {
        var faulted = Task.FromException(
            new InvalidOperationException("Injected unobserved Task exception gate probe."));
        GC.KeepAlive(faulted);
    }

    private static MemorySample CaptureResourceSample(double elapsedSeconds)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedBytes = GC.GetTotalMemory(forceFullCollection: false);
        var gc = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySample(
            DateTimeOffset.UtcNow,
            elapsedSeconds,
            retainedBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            gc.HeapSizeBytes,
            gc.TotalCommittedBytes,
            gc.FragmentedBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.Threads.Count,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount,
            PooledAsyncStreamDispatcher<int>.RetainedCountForTests,
            UnobservedTaskExceptions: 0);
    }

    private static double? CalculateWindowGrowth(
        IReadOnlyList<MemorySample> samples,
        TimeSpan window)
    {
        if (samples.Count == 0)
            return null;
        var last = samples[^1];
        if (last.ElapsedSeconds < window.TotalSeconds)
            return null;
        var windowStart = Math.Max(0, last.ElapsedSeconds - window.TotalSeconds);
        var baseline = samples[0];
        for (var index = 0; index < samples.Count; index++)
        {
            if (samples[index].ElapsedSeconds < windowStart)
                continue;
            baseline = samples[index];
            break;
        }
        return baseline.RetainedBytes == 0
            ? 0
            : (last.RetainedBytes - baseline.RetainedBytes) * 100.0 / baseline.RetainedBytes;
    }

    private static string GetCommit()
    {
        try
        {
            var info = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null)
                return "unknown";
            var result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? result : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool? GetWorkingTreeDirty()
    {
        try
        {
            var info = new ProcessStartInfo("git", "status --porcelain")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var result = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? !string.IsNullOrEmpty(result) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ChaosDiagnosticArtifact> CaptureProcessDumpAsync(string? reportPath)
    {
        var dumpPath = string.IsNullOrWhiteSpace(reportPath)
            ? Path.GetFullPath($"artifacts/chaos/chaos-failure-{Environment.ProcessId}.dmp")
            : Path.ChangeExtension(Path.GetFullPath(reportPath), ".dmp");
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
        var executableName = OperatingSystem.IsWindows() ? "createdump.exe" : "createdump";
        var toolPath = Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            executableName);
        if (!File.Exists(toolPath))
        {
            return new ChaosDiagnosticArtifact(
                "ProcessDump",
                dumpPath,
                false,
                $"Runtime dump tool was not found at {toolPath}.");
        }

        try
        {
            var info = new ProcessStartInfo(toolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("--withheap");
            info.ArgumentList.Add("--crashreport");
            info.ArgumentList.Add("--name");
            info.ArgumentList.Add(dumpPath);
            info.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(info);
            if (process is null)
            {
                return new ChaosDiagnosticArtifact(
                    "ProcessDump", dumpPath, false, "Failed to start the runtime dump tool.");
            }
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                _ = await ReadDumpOutputAsync(standardOutput, standardError).ConfigureAwait(false);
                return new ChaosDiagnosticArtifact(
                    "ProcessDump", dumpPath, false, "Runtime dump capture exceeded 30 seconds.");
            }
            var output = await ReadDumpOutputAsync(standardOutput, standardError).ConfigureAwait(false);
            return new ChaosDiagnosticArtifact(
                "ProcessDump",
                dumpPath,
                process.ExitCode == 0 && File.Exists(dumpPath),
                output);
        }
        catch (Exception exception)
        {
            return new ChaosDiagnosticArtifact(
                "ProcessDump", dumpPath, false, exception.ToString());
        }
    }

    private static async Task<string> ReadDumpOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        try
        {
            var output = ((await standardOutput.ConfigureAwait(false)) + Environment.NewLine +
                          (await standardError.ConfigureAwait(false))).Trim();
            return output.Length > 4096 ? output[..4096] : output;
        }
        catch (Exception exception)
        {
            return $"Failed to read dump-tool output: {exception.Message}";
        }
    }

    private static void WriteReport(string? path, ChaosReport report)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.tmp-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, fullPath, overwrite: true);
        Console.WriteLine($"CHAOS_REPORT {fullPath}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.ChaosTests options:");
        Console.WriteLine("  --duration 10m                  (supports s, m, h, d, or TimeSpan)");
        Console.WriteLine("  --duration-seconds 120");
        Console.WriteLine("  --transport tcp|sharedmemory");
        Console.WriteLine("  --shm-name sharplink-chaos");
        Console.WriteLine("  --concurrency 32");
        Console.WriteLine("  --restart-interval-seconds 5");
        Console.WriteLine("  --checkpoint-interval 1m");
        Console.WriteLine("  --checkpoint-interval-seconds 60");
        Console.WriteLine("  --dump-on-failure true");
        Console.WriteLine("  --stop-on-unexpected true");
        Console.WriteLine("  --inject-client-error false      (release-gate self-test)");
        Console.WriteLine("  --inject-server-error false      (release-gate self-test)");
        Console.WriteLine("  --inject-unobserved-task-exception false (release-gate self-test)");
        Console.WriteLine("  --json-output artifacts/chaos/report.json");
    }
}
