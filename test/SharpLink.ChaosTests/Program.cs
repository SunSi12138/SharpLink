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
        var serverStops = new ConcurrentQueue<ChaosServerStopObservation>();
        var reportGate = new Lock();
        var phase = "Starting";
        var soakStarted = Stopwatch.GetTimestamp();
        var startedMemory = 0L;
        long success = 0;
        long expectedFailures = 0;
        long unexpectedFailures = 0;
        long faultGeneration = 0;
        long maxRecoveryMilliseconds = 0;
        var restartCount = 0;
        ChaosDiagnosticArtifact? diagnosticArtifact = null;
        Task<ChaosDiagnosticArtifact>? diagnosticCaptureTask = null;
        var diagnosticGate = new Lock();
        using var clientLogs = new ChaosLoggerFactory();

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

        phase = "StartingServer";
        var server = await ChaosServer.StartAsync(
            options.Transport,
            options.SharedMemoryName,
            port: 0).ConfigureAwait(false);
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
        startedMemory = GetRetainedMemory();
        soakStarted = Stopwatch.GetTimestamp();
        memorySamples.Enqueue(new MemorySample(DateTimeOffset.UtcNow, 0, startedMemory));
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
        var endedMemory = GetRetainedMemory();
        var memoryGrowthPercent = startedMemory == 0
            ? 0
            : (endedMemory - startedMemory) * 100.0 / startedMemory;
        memorySamples.Enqueue(new MemorySample(
            DateTimeOffset.UtcNow,
            Stopwatch.GetElapsedTime(soakStarted).TotalSeconds,
            endedMemory));
        var orderedMemorySamples = memorySamples.OrderBy(static sample => sample.ElapsedSeconds).ToArray();
        var lastSixHoursGrowthPercent = CalculateWindowGrowth(orderedMemorySamples, TimeSpan.FromHours(6));
        if (options.InjectClientError)
            clientLogs.InjectErrorForGateProbe();
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
        else if (clientLogs.ErrorCount != 0)
        {
            exitCode = 2;
            terminalFailure = new ChaosFailure(
                "ClientErrorLogs",
                $"Chaos captured {clientLogs.ErrorCount} client Error log(s).",
                string.Join(Environment.NewLine, clientLogs.AllSnapshot()));
        }
        else if (success == 0 || restartCount == 0)
        {
            exitCode = 3;
            terminalFailure = new ChaosFailure(
                "InsufficientCoverage",
                $"Chaos completed with success={success} and restarts={restartCount}.",
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
        TryWriteReport(
            exitCode == 0 ? "Passed" : "Failed",
            phase,
            exitCode,
            terminalFailure,
            drain,
            isFinal: true);
        AppDomain.CurrentDomain.UnhandledException -= unhandledHandler;

        Console.WriteLine(
            $"CHAOS_RESULT success={success} injected={expectedFailures} unexpected={unexpectedFailures} " +
            $"restarts={restartCount} retained={startedMemory}->{endedMemory} ({memoryGrowthPercent:F2}%)");
        return exitCode;

        async Task RestartLoopAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(options.RestartInterval, duration.Token).ConfigureAwait(false);
                    clientLogs.Clear();
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
                    var sample = new MemorySample(
                        DateTimeOffset.UtcNow,
                        Stopwatch.GetElapsedTime(soakStarted).TotalSeconds,
                        GetRetainedMemory());
                    memorySamples.Enqueue(sample);
                    TryWriteReport("Running", phase, null, failure: null, drain: null, isFinal: false);
                    Console.WriteLine(
                        $"CHAOS_CHECKPOINT elapsed={sample.ElapsedSeconds:F0}s success={Volatile.Read(ref success)} " +
                        $"unexpected={Volatile.Read(ref unexpectedFailures)} restarts={Volatile.Read(ref restartCount)} " +
                        $"retained={sample.RetainedBytes}");
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
                Volatile.Read(ref expectedFailures),
                Volatile.Read(ref unexpectedFailures),
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
                clientLogs.AllSnapshot(),
                [.. serverStops]);
        }

        void TryWriteReport(
            string status,
            string currentPhase,
            int? currentExitCode,
            ChaosFailure? failure,
            ChaosDrainResult? drain,
            bool isFinal)
        {
            if (string.IsNullOrWhiteSpace(options.JsonOutputPath))
                return;
            try
            {
                lock (reportGate)
                {
                    WriteReport(
                        options.JsonOutputPath,
                        CreateReport(status, currentPhase, currentExitCode, failure, drain, isFinal));
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"CHAOS_REPORT_WRITE_FAILED type={exception.GetType().FullName} message={exception.Message}");
            }
        }
    }

    private static async Task RunWorkerAsync(
        IChaosService service,
        int workerId,
        CancellationToken runToken,
        Func<long> getFaultGeneration,
        Action success,
        Action expectedFailure,
        Action<Exception> unexpectedFailure)
    {
        var iteration = 0;
        while (!runToken.IsCancellationRequested)
        {
            var operation = (workerId + iteration++) & 3;
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
                    default:
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
            await foreach (var _ in service.StreamAsync(8, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
            }
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

    private static long GetRetainedMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
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
        Console.WriteLine("  --json-output artifacts/chaos/report.json");
    }
}

internal sealed class ChaosServer(SharpLinkServer server, Task runTask, int port)
{
    internal int Port { get; } = port;

    internal static Task<ChaosServer> StartAsync(
        ChaosTransport transport,
        string sharedMemoryName,
        int port)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        if (transport == ChaosTransport.SharedMemory)
            builder.UseSharedMemory(sharedMemoryName);
        else
            builder.UseTcp(port, IPAddress.Loopback.ToString());
        var boundPort = transport == ChaosTransport.Tcp
            ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port
            : 0;
        var server = (SharpLinkServer)builder.Build();
        var runTask = server.RunAsync().AsTask();
        return Task.FromResult(new ChaosServer(server, runTask, boundPort));
    }

    internal static async Task<ChaosServer> StartWithRetryAsync(
        ChaosTransport transport,
        string sharedMemoryName,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await StartAsync(transport, sharedMemoryName, port).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastException = exception;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("TCP listener did not become reusable after rolling restart.", lastException);
    }

    internal async Task<ChaosServerStopObservation> StopAsync(string reason)
    {
        await server.StopAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        await runTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        return new ChaosServerStopObservation(
            DateTimeOffset.UtcNow,
            reason,
            server.ActiveCallCountForDiagnostics,
            server.LastStopDiagnostics);
    }
}

internal sealed record ChaosServerStopObservation(
    DateTimeOffset TimestampUtc,
    string Reason,
    int ActiveCallsAfterStop,
    ServerStopDiagnosticSnapshot? GraceTimeoutSnapshot);

internal sealed class ChaosMetricObserver : IDisposable
{
    private static readonly string[] Tracked =
    [
        "sharplink.connections.active",
        "sharplink.calls.active",
        "sharplink.requests.pending",
        "sharplink.streams.active",
        "sharplink.send.queue.bytes"
    ];

    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ActiveCallKey, long> _activeCallBreakdown = new();
    private readonly MeterListener _listener = new();

    internal ChaosMetricObserver()
    {
        for (var index = 0; index < Tracked.Length; index++)
            _values[Tracked[index]] = 0;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && _values.ContainsKey(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _values.AddOrUpdate(
                instrument.Name,
                static (_, delta) => delta,
                static (_, value, delta) => value + delta,
                measurement);
            if (instrument.Name != "sharplink.calls.active")
                return;

            var side = "unknown";
            var contractId = long.MinValue;
            var methodId = long.MinValue;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "rpc.side":
                        if (tag.Value is string configuredSide)
                            side = configuredSide;
                        break;
                    case "rpc.sharplink.contract_id":
                        _ = TryReadInt64(tag.Value, out contractId);
                        break;
                    case "rpc.sharplink.method_id":
                        _ = TryReadInt64(tag.Value, out methodId);
                        break;
                }
            }
            var key = new ActiveCallKey(side, contractId, methodId);
            _activeCallBreakdown.AddOrUpdate(
                key,
                static (_, delta) => delta,
                static (_, value, delta) => value + delta,
                measurement);
        });
        _listener.Start();

    }

    internal IReadOnlyDictionary<string, long> Snapshot()
        => _values.ToDictionary(static value => value.Key, static value => value.Value);

    internal IReadOnlyDictionary<string, long> ActiveCallBreakdownSnapshot()
        => _activeCallBreakdown
            .Where(static value => value.Value != 0)
            .ToDictionary(
                static value => value.Key.ToString(),
                static value => value.Value,
                StringComparer.Ordinal);

    private static bool TryReadInt64(object? value, out long result)
    {
        switch (value)
        {
            case long signed:
                result = signed;
                return true;
            case ulong unsigned when unsigned <= long.MaxValue:
                result = (long)unsigned;
                return true;
            case int signed32:
                result = signed32;
                return true;
            case uint unsigned32:
                result = unsigned32;
                return true;
            default:
                result = long.MinValue;
                return false;
        }
    }

    internal async Task<ChaosDrainResult> WaitForZeroAsync(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (_values.Any(static value => value.Value != 0))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return CreateDrainResult(drained: false, started);
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        return CreateDrainResult(drained: true, started);
    }

    private ChaosDrainResult CreateDrainResult(bool drained, long started)
        => new(
            drained,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            Snapshot(),
            ActiveCallBreakdownSnapshot());

    public void Dispose() => _listener.Dispose();

    private readonly record struct ActiveCallKey(string Side, long ContractId, long MethodId)
    {
        public override string ToString()
            => $"{Side}:{FormatIdentifier(ContractId)}:{FormatIdentifier(MethodId)}";

        private static string FormatIdentifier(long value)
            => value == long.MinValue
                ? "unknown"
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class ChaosLoggerFactory : ILoggerFactory, ILogger
{
    private const int MaxRetainedErrors = 8;
    private readonly ConcurrentQueue<string> _generationErrors = new();
    private readonly ConcurrentQueue<string> _allErrors = new();
    private long _errorCount;

    internal long ErrorCount => Volatile.Read(ref _errorCount);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return this;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        RecordError(
            $"Event={eventId.Id}:{eventId.Name}; Message={formatter(state, exception)}; " +
            $"Exception={exception}");
    }

    internal void Clear() => _generationErrors.Clear();

    internal IReadOnlyList<string> Snapshot() => [.. _generationErrors];

    internal IReadOnlyList<string> AllSnapshot() => [.. _allErrors];

    internal void InjectErrorForGateProbe()
        => RecordError("Injected client Error for the Chaos release-gate self-test.");

    private void RecordError(string error)
    {
        Interlocked.Increment(ref _errorCount);
        EnqueueBounded(_generationErrors, error);
        EnqueueBounded(_allErrors, error);
    }

    private static void EnqueueBounded(ConcurrentQueue<string> queue, string error)
    {
        queue.Enqueue(error);
        while (queue.Count > MaxRetainedErrors)
            queue.TryDequeue(out _);
    }

    public void Dispose()
    {
        _generationErrors.Clear();
        _allErrors.Clear();
    }
}

internal sealed record ChaosDrainResult(
    bool Drained,
    double WaitedSeconds,
    IReadOnlyDictionary<string, long> Metrics,
    IReadOnlyDictionary<string, long> ActiveCallBreakdown)
{
    internal string Describe()
        => "SharpLink state did not drain after chaos: " +
           string.Join(", ", Metrics.Select(static value => $"{value.Key}={value.Value}")) +
           "; active-call breakdown: " +
           string.Join(", ", ActiveCallBreakdown.Select(static value => $"{value.Key}={value.Value}"));
}

internal sealed class ChaosOptions
{
    internal TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(120);
    internal int Concurrency { get; private init; } = 32;
    internal TimeSpan RestartInterval { get; private init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CheckpointInterval { get; private init; } = TimeSpan.FromSeconds(30);
    internal bool DumpOnFailure { get; private init; } = true;
    internal bool StopOnUnexpectedFailure { get; private init; } = true;
    internal bool InjectClientError { get; private init; }
    internal ChaosTransport Transport { get; private init; } = ChaosTransport.Tcp;
    internal string SharedMemoryName { get; private init; } = "sharplink-chaos";
    internal string? JsonOutputPath { get; private init; }

    internal static ChaosOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for '{argument}'.");
            values[argument[2..]] = args[index];
        }

        if (values.ContainsKey("duration") && values.ContainsKey("duration-seconds"))
            throw new ArgumentException("Use either --duration or --duration-seconds, not both.");
        var duration = values.TryGetValue("duration", out var durationText)
            ? ParseDuration(durationText, "duration")
            : TimeSpan.FromSeconds(ParsePositive(values, "duration-seconds", 120));
        var concurrency = ParsePositive(values, "concurrency", 32);
        var restartSeconds = ParsePositive(values, "restart-interval-seconds", 5);
        var transport = values.GetValueOrDefault("transport", "tcp").ToLowerInvariant() switch
        {
            "tcp" => ChaosTransport.Tcp,
            "sharedmemory" or "shared-memory" or "shm" => ChaosTransport.SharedMemory,
            var value => throw new ArgumentException($"Unsupported chaos transport '{value}'.")
        };
        if (TimeSpan.FromSeconds(restartSeconds) >= duration)
            throw new ArgumentException("Restart interval must be shorter than the chaos duration.");
        var checkpointInterval = values.TryGetValue("checkpoint-interval", out var checkpointText)
            ? ParseDuration(checkpointText, "checkpoint-interval")
            : values.TryGetValue("checkpoint-interval-seconds", out var checkpointSecondsText)
                ? TimeSpan.FromSeconds(ParsePositive(checkpointSecondsText, "checkpoint-interval-seconds"))
                : GetDefaultCheckpointInterval(duration);
        if (checkpointInterval >= duration)
            checkpointInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, duration.Ticks / 2));
        return new ChaosOptions
        {
            Duration = duration,
            Concurrency = concurrency,
            RestartInterval = TimeSpan.FromSeconds(restartSeconds),
            CheckpointInterval = checkpointInterval,
            DumpOnFailure = ParseBoolean(values, "dump-on-failure", fallback: true),
            StopOnUnexpectedFailure = ParseBoolean(values, "stop-on-unexpected", fallback: true),
            InjectClientError = ParseBoolean(values, "inject-client-error", fallback: false),
            Transport = transport,
            SharedMemoryName = values.GetValueOrDefault("shm-name", "sharplink-chaos"),
            JsonOutputPath = values.GetValueOrDefault("json-output")
        };
    }

    private static int ParsePositive(Dictionary<string, string> values, string name, int fallback)
    {
        var value = int.Parse(values.GetValueOrDefault(name, fallback.ToString()));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }

    private static int ParsePositive(string text, string name)
    {
        var value = int.Parse(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }

    private static TimeSpan ParseDuration(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var unitLength = char.IsLetter(value[^1]) ? 1 : 0;
        if (unitLength == 1 && double.TryParse(
                value.AsSpan(0, value.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var amount))
        {
            var duration = char.ToLowerInvariant(value[^1]) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new ArgumentException(
                    $"Unsupported {name} unit in '{value}'. Use s, m, h, d, or a TimeSpan.",
                    name)
            };
            if (duration > TimeSpan.Zero)
                return duration;
        }
        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > TimeSpan.Zero)
        {
            return parsed;
        }
        throw new ArgumentException($"{name} must be a positive duration such as 10m, 24h, or 00:10:00.", name);
    }

    private static TimeSpan GetDefaultCheckpointInterval(TimeSpan duration)
    {
        if (duration >= TimeSpan.FromHours(12))
            return TimeSpan.FromMinutes(30);
        if (duration >= TimeSpan.FromHours(6))
            return TimeSpan.FromMinutes(15);
        if (duration >= TimeSpan.FromHours(1))
            return TimeSpan.FromMinutes(10);
        if (duration >= TimeSpan.FromMinutes(10))
            return TimeSpan.FromMinutes(1);
        if (duration >= TimeSpan.FromMinutes(2))
            return TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(10);
    }

    private static bool ParseBoolean(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool fallback)
    {
        if (!values.TryGetValue(name, out var value))
            return fallback;
        if (bool.TryParse(value, out var parsed))
            return parsed;
        throw new ArgumentException($"{name} must be true or false.", name);
    }
}

internal sealed record ChaosReport(
    DateTimeOffset TimestampUtc,
    DateTimeOffset StartedUtc,
    string Status,
    string Phase,
    int? ExitCode,
    bool IsFinal,
    string Commit,
    bool? WorkingTreeDirty,
    string OperatingSystem,
    string Architecture,
    string Runtime,
    double DurationSeconds,
    double ActualElapsedSeconds,
    double CheckpointIntervalSeconds,
    double RestartIntervalSeconds,
    int Concurrency,
    string Transport,
    bool DumpOnFailure,
    bool StopOnUnexpectedFailure,
    int RestartCount,
    long Success,
    long ExpectedFailures,
    long UnexpectedFailures,
    long MaxRecoveryMilliseconds,
    long RetainedMemoryStart,
    long RetainedMemoryEnd,
    double RetainedMemoryGrowthPercent,
    double? LastSixHoursRetainedMemoryGrowthPercent,
    IReadOnlyList<MemorySample> MemorySamples,
    IReadOnlyDictionary<string, long> FinalMetrics,
    IReadOnlyDictionary<string, long> ActiveCallBreakdown,
    ChaosDrainResult? Drain,
    ChaosFailure? TerminalFailure,
    ChaosDiagnosticArtifact? DiagnosticArtifact,
    IReadOnlyDictionary<string, long> Failures,
    IReadOnlyList<string> FailureSamples,
    IReadOnlyList<string> ClientErrors,
    IReadOnlyList<ChaosServerStopObservation> ServerStops);

internal sealed record ChaosFailure(string Type, string Message, string? Details)
{
    internal static ChaosFailure FromException(Exception exception)
        => new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.ToString());
}

internal sealed record ChaosDiagnosticArtifact(
    string Kind,
    string Path,
    bool Captured,
    string Details);

internal enum ChaosTransport
{
    Tcp,
    SharedMemory
}

internal sealed record MemorySample(
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    long RetainedBytes);

[RpcContract]
public interface IChaosService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken);

    [NonCancellable]
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);

    IAsyncEnumerable<int> StreamAsync(int count, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ChaosService : IChaosService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken)
        => await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        var count = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            sum += value;
            count++;
        }
        if (count != 16)
            throw new InvalidDataException($"Server received only {count}/16 client-stream items.");
        return sum;
    }

    public async IAsyncEnumerable<int> StreamAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            await Task.Yield();
        }
    }
}
