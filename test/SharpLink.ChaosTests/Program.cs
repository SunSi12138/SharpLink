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
        using var metrics = new ChaosMetricObserver();
        using var duration = new CancellationTokenSource(options.Duration);
        var failures = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var failureSamples = new ConcurrentQueue<string>();
        var startedMemory = 0L;
        long success = 0;
        long expectedFailures = 0;
        long unexpectedFailures = 0;
        long faultGeneration = 0;
        long maxRecoveryMilliseconds = 0;
        var restartCount = 0;

        var server = await ChaosServer.StartAsync(port: 0).ConfigureAwait(false);
        var port = server.Port;
        await using var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
            .UseRequestTimeout(TimeSpan.FromSeconds(2))
            .UseConnectionPool(pool =>
            {
                pool.MinConnections = 1;
                pool.MaxConnections = Math.Min(Environment.ProcessorCount, 4);
            })
            .Build();

        await client.ConnectAsync(duration.Token).ConfigureAwait(false);
        var service = client.Get<IChaosService>();
        await WarmUpAsync(service, duration.Token).ConfigureAwait(false);
        startedMemory = GetRetainedMemory();
        var soakStarted = Stopwatch.GetTimestamp();
        var memorySamples = new ConcurrentQueue<MemorySample>();
        memorySamples.Enqueue(new MemorySample(DateTimeOffset.UtcNow, 0, startedMemory));
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

        await client.StopAsync().ConfigureAwait(false);
        await server.StopAsync().ConfigureAwait(false);
        await metrics.WaitForZeroAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
        var report = new ChaosReport(
            DateTimeOffset.UtcNow,
            GetCommit(),
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            options.Duration.TotalSeconds,
            options.Concurrency,
            restartCount,
            success,
            expectedFailures,
            unexpectedFailures,
            maxRecoveryMilliseconds,
            startedMemory,
            endedMemory,
            memoryGrowthPercent,
            lastSixHoursGrowthPercent,
            orderedMemorySamples,
            metrics.Snapshot(),
            failures.OrderByDescending(static failure => failure.Value)
                .ToDictionary(static failure => failure.Key, static failure => failure.Value),
            [.. failureSamples]);
        WriteReport(options.JsonOutputPath, report);

        Console.WriteLine(
            $"CHAOS_RESULT success={success} injected={expectedFailures} unexpected={unexpectedFailures} " +
            $"restarts={restartCount} retained={startedMemory}->{endedMemory} ({memoryGrowthPercent:F2}%)");
        if (unexpectedFailures != 0)
            return 2;
        if (success == 0 || restartCount == 0)
            return 3;
        if (options.Duration >= TimeSpan.FromHours(6) && lastSixHoursGrowthPercent > 5)
            return 4;
        return 0;

        async Task RestartLoopAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(options.RestartInterval, duration.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref faultGeneration);
                    var recoveryStarted = Stopwatch.GetTimestamp();
                    using var recoveryTimeout = new CancellationTokenSource(RecoveryTimeout);
                    await server.StopAsync().ConfigureAwait(false);
                    try
                    {
                        server = await ChaosServer.StartWithRetryAsync(port, recoveryTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (recoveryTimeout.IsCancellationRequested)
                    {
                        RecordUnexpectedFailure(CreateRecoveryTimeoutException());
                        await duration.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    Interlocked.Increment(ref restartCount);
                    if (!await WaitForRecoveryAsync(service, recoveryTimeout.Token).ConfigureAwait(false))
                    {
                        RecordUnexpectedFailure(CreateRecoveryTimeoutException());
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
            Interlocked.Increment(ref unexpectedFailures);
            var key = DescribeFailure(exception);
            failures.AddOrUpdate(key, 1, static (_, count) => count + 1);
            if (failureSamples.Count < 20)
                failureSamples.Enqueue(exception.ToString());
        }

        async Task SampleRetainedMemoryAsync()
        {
            var interval = options.Duration >= TimeSpan.FromHours(12)
                ? TimeSpan.FromMinutes(30)
                : options.Duration >= TimeSpan.FromHours(6)
                    ? TimeSpan.FromMinutes(15)
                    : options.Duration >= TimeSpan.FromHours(1)
                        ? TimeSpan.FromMinutes(10)
                        : Timeout.InfiniteTimeSpan;
            if (interval == Timeout.InfiniteTimeSpan)
                return;

            try
            {
                while (true)
                {
                    await Task.Delay(interval, duration.Token).ConfigureAwait(false);
                    memorySamples.Enqueue(new MemorySample(
                        DateTimeOffset.UtcNow,
                        Stopwatch.GetElapsedTime(soakStarted).TotalSeconds,
                        GetRetainedMemory()));
                }
            }
            catch (OperationCanceledException) when (duration.IsCancellationRequested)
            {
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
                            cancellation.CancelAfter(TimeSpan.FromMilliseconds(2));
                            await service.DelayAsync(100, cancellation.Token).ConfigureAwait(false);
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

    private static TimeoutException CreateRecoveryTimeoutException()
        => new($"Client did not complete a probe RPC within {RecoveryTimeout.TotalSeconds:F0} " +
               "seconds of a server restart.");

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

    private static double CalculateWindowGrowth(
        IReadOnlyList<MemorySample> samples,
        TimeSpan window)
    {
        if (samples.Count == 0)
            return 0;
        var last = samples[^1];
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

    private static void WriteReport(string? path, ChaosReport report)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Console.WriteLine($"CHAOS_REPORT {fullPath}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpLink.ChaosTests options:");
        Console.WriteLine("  --duration-seconds 120");
        Console.WriteLine("  --concurrency 32");
        Console.WriteLine("  --restart-interval-seconds 5");
        Console.WriteLine("  --json-output artifacts/chaos/report.json");
    }
}

internal sealed class ChaosServer(ISharpLinkServer server, Task runTask, int port)
{
    internal int Port { get; } = port;

    internal static Task<ChaosServer> StartAsync(int port)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(port, IPAddress.Loopback.ToString())
            .UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
            .AddService<IChaosService, ChaosService>();
        var boundPort = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        var server = builder.Build();
        var runTask = server.RunAsync().AsTask();
        return Task.FromResult(new ChaosServer(server, runTask, boundPort));
    }

    internal static async Task<ChaosServer> StartWithRetryAsync(int port, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await StartAsync(port).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastException = exception;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("TCP listener did not become reusable after rolling restart.", lastException);
    }

    internal async Task StopAsync()
    {
        await server.StopAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        await runTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
    }
}

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
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            _values.AddOrUpdate(instrument.Name, measurement, (_, value) => value + measurement));
        _listener.Start();
    }

    internal IReadOnlyDictionary<string, long> Snapshot()
        => _values.ToDictionary(static value => value.Key, static value => value.Value);

    internal async Task WaitForZeroAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (_values.Any(static value => value.Value != 0))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new InvalidOperationException(
                    "SharpLink state did not drain after chaos: " +
                    string.Join(", ", _values.Select(static value => $"{value.Key}={value.Value}")));
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    public void Dispose() => _listener.Dispose();
}

internal sealed class ChaosOptions
{
    internal TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(120);
    internal int Concurrency { get; private init; } = 32;
    internal TimeSpan RestartInterval { get; private init; } = TimeSpan.FromSeconds(5);
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

        var durationSeconds = ParsePositive(values, "duration-seconds", 120);
        var concurrency = ParsePositive(values, "concurrency", 32);
        var restartSeconds = ParsePositive(values, "restart-interval-seconds", 5);
        if (restartSeconds >= durationSeconds)
            throw new ArgumentException("Restart interval must be shorter than the chaos duration.");
        return new ChaosOptions
        {
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Concurrency = concurrency,
            RestartInterval = TimeSpan.FromSeconds(restartSeconds),
            JsonOutputPath = values.GetValueOrDefault("json-output")
        };
    }

    private static int ParsePositive(Dictionary<string, string> values, string name, int fallback)
    {
        var value = int.Parse(values.GetValueOrDefault(name, fallback.ToString()));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }
}

internal sealed record ChaosReport(
    DateTimeOffset TimestampUtc,
    string Commit,
    string OperatingSystem,
    string Architecture,
    string Runtime,
    double DurationSeconds,
    int Concurrency,
    int RestartCount,
    long Success,
    long ExpectedFailures,
    long UnexpectedFailures,
    long MaxRecoveryMilliseconds,
    long RetainedMemoryStart,
    long RetainedMemoryEnd,
    double RetainedMemoryGrowthPercent,
    double LastSixHoursRetainedMemoryGrowthPercent,
    IReadOnlyList<MemorySample> MemorySamples,
    IReadOnlyDictionary<string, long> FinalMetrics,
    IReadOnlyDictionary<string, long> Failures,
    IReadOnlyList<string> FailureSamples);

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
