using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Client;

namespace SharpLink.Benchmarks;

internal static class ClientCallShapeEvidenceRunner
{
    private static readonly JsonSerializerOptions SJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 5)
        {
            throw new ArgumentException(
                "Usage: --client-call-shape-evidence <scenario> " +
                "<warmup-operations> <measurement-seconds> <max-operations> <output-json>");
        }

        var scenario = Enum.Parse<ClientCallShapeScenario>(args[0], ignoreCase: true);
        var warmupOperations = int.Parse(args[1], CultureInfo.InvariantCulture);
        var measurementSeconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        var maxOperations = int.Parse(args[3], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[4]);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupOperations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measurementSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);

        await using var benchmark = await ClientCallShapeCase.CreateAsync(scenario)
            .ConfigureAwait(false);

        var firstStarted = Stopwatch.GetTimestamp();
        var firstResult = await benchmark.InvokeAsync().ConfigureAwait(false);
        var firstCallUs = Stopwatch.GetElapsedTime(firstStarted).TotalMicroseconds;
        Validate(firstResult, benchmark.ExpectedResult, scenario, "first call");

        for (var operation = 0; operation < warmupOperations; operation++)
        {
            var result = await benchmark.InvokeAsync().ConfigureAwait(false);
            Validate(result, benchmark.ExpectedResult, scenario, "warmup");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var latencies = new long[maxOperations];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var measurementStarted = Stopwatch.GetTimestamp();
        var measurementDeadline = measurementStarted +
            checked((long)Math.Ceiling(measurementSeconds * Stopwatch.Frequency));
        var completed = 0;
        long latencyTicks = 0;

        while (completed < latencies.Length &&
               Stopwatch.GetTimestamp() < measurementDeadline)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await benchmark.InvokeAsync().ConfigureAwait(false);
            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            Validate(result, benchmark.ExpectedResult, scenario, "measurement");
            latencies[completed++] = elapsedTicks;
            latencyTicks += elapsedTicks;
        }

        var measurementElapsed = Stopwatch.GetElapsedTime(measurementStarted);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (completed == 0)
            throw new InvalidOperationException("The call-shape evidence run completed no operations.");

        Array.Sort(latencies, 0, completed);
        var resultDocument = new ClientCallShapeEvidenceResult
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Scenario = scenario.ToString(),
            Shape = benchmark.Shape,
            FactSummary = benchmark.FactSummary,
            PayloadClass = benchmark.PayloadClass,
            TimestampUtc = DateTimeOffset.UtcNow,
            HostName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            TieredCompilation =
                Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
            WarmupOperations = warmupOperations,
            RequestedMeasurementSeconds = measurementSeconds,
            ActualMeasurementSeconds = measurementElapsed.TotalSeconds,
            Operations = completed,
            ThroughputPerSecond = completed / measurementElapsed.TotalSeconds,
            FirstCallUs = firstCallUs,
            AverageUs = TicksToMicroseconds(latencyTicks / (double)completed),
            P50Us = Percentile(latencies, completed, 50),
            P99Us = Percentile(latencies, completed, 99),
            P999Us = Percentile(latencies, completed, 99.9),
            MaxUs = TicksToMicroseconds(latencies[completed - 1]),
            CpuUsPerOperation = (cpuAfter - cpuBefore).TotalMicroseconds / completed,
            AllocatedBytesPerOperation = (allocatedAfter - allocatedBefore) / (double)completed,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            ThreadCount = process.Threads.Count,
            WorkingSetBytes = process.WorkingSet64,
            ValidationFailures = 0,
            HitOperationLimit = completed == maxOperations
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(resultDocument, SJsonOptions)).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(resultDocument, SJsonOptions));
    }

    private static double Percentile(long[] values, int count, double percentile)
    {
        var rank = Math.Clamp(
            (int)Math.Ceiling(percentile / 100 * count) - 1,
            0,
            count - 1);
        return TicksToMicroseconds(values[rank]);
    }

    private static double TicksToMicroseconds(double ticks)
        => ticks * 1_000_000d / Stopwatch.Frequency;

    private static void Validate(
        long actual,
        long expected,
        ClientCallShapeScenario scenario,
        string phase)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{scenario} returned {actual} instead of {expected} during {phase}.");
        }
    }
}

internal enum ClientCallShapeScenario
{
    UnaryIdempotentValuePayload,
    UnaryNonIdempotentValuePayload,
    UnaryTimedIdempotentValuePayload,
    UnaryCancellableValuePayload,
    UnaryNoPayloadValue,
    UnaryNoPayloadNoResponse,
    UnaryRequiredReference,
    UnaryNullableReference,
    UnaryBytes4096,
    OneWayPayload,
    OneWayTimedPayload,
    OneWayCancellablePayload,
    OneWayOneClientStream,
    OneWayTwoClientStreamsTimed,
    ClientStreamingOneStream,
    ClientStreamingTwoStreams,
    ClientStreamingCancellable,
    ClientStreamingPayload4096,
    ServerStreamingValue,
    ServerStreamingCancellable,
    ServerStreamingPayload4096,
    DuplexRequiredReference,
    DuplexCancellable,
    DuplexPayload4096
}

internal sealed class ClientCallShapeCase : IAsyncDisposable
{
    private static readonly int[] SNumbers = [1, 2, 3];
    private static readonly string[] SStrings = ["a", "bb", "ccc"];

    private readonly BenchmarkEnvironment _environment;
    private readonly CancellationTokenSource _cancellation;

    private ClientCallShapeCase(
        BenchmarkEnvironment environment,
        CancellationTokenSource cancellation,
        string shape,
        string factSummary,
        string payloadClass,
        long expectedResult,
        Func<ValueTask<long>> invokeAsync)
    {
        _environment = environment;
        _cancellation = cancellation;
        Shape = shape;
        FactSummary = factSummary;
        PayloadClass = payloadClass;
        ExpectedResult = expectedResult;
        InvokeAsync = invokeAsync;
    }

    public string Shape { get; }
    public string FactSummary { get; }
    public string PayloadClass { get; }
    public long ExpectedResult { get; }
    public Func<ValueTask<long>> InvokeAsync { get; }

    public static async Task<ClientCallShapeCase> CreateAsync(
        ClientCallShapeScenario scenario)
    {
        var environment = await BenchmarkEnvironment.CreateAsync(
            createClientBuilder: port => SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseRetry()).ConfigureAwait(false);
        var cancellation = new CancellationTokenSource();
        try
        {
            var rpc = environment.Rpc;
            var token = cancellation.Token;
            var payload4096 = BenchmarkRpcService.GetPayload(4096);
            var payloadStream = new ReusableAsyncEnumerable<byte[]>([payload4096]);
            var numbers = new ReusableAsyncEnumerable<int>(SNumbers);
            var leftNumbers = new ReusableAsyncEnumerable<int>(SNumbers);
            var rightNumbers = new ReusableAsyncEnumerable<int>(SNumbers);
            var strings = new ReusableAsyncEnumerable<string>(SStrings);
            var cancellableStrings = new ReusableAsyncEnumerable<string>(SStrings);

            var descriptor = scenario switch
            {
                ClientCallShapeScenario.UnaryIdempotentValuePayload => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=value,idempotent=yes,timeout=no,cancel=no,streams=0",
                    "tiny",
                    30,
                    async () => await rpc.AddAsync(10, 20).ConfigureAwait(false)),

                ClientCallShapeScenario.UnaryNonIdempotentValuePayload => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=value,idempotent=no,timeout=no,cancel=no,streams=0",
                    "tiny",
                    30,
                    async () => await rpc.AddNonIdempotentAsync(10, 20).ConfigureAwait(false)),

                ClientCallShapeScenario.UnaryTimedIdempotentValuePayload => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=value,idempotent=yes,timeout=yes,cancel=no,streams=0",
                    "tiny",
                    30,
                    async () => await rpc.AddTimedIdempotentAsync(10, 20).ConfigureAwait(false)),

                ClientCallShapeScenario.UnaryCancellableValuePayload => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=value,idempotent=no,timeout=no,cancel=yes,streams=0",
                    "tiny",
                    30,
                    async () => await rpc.AddCancellableAsync(10, 20, token).ConfigureAwait(false)),

                ClientCallShapeScenario.UnaryNoPayloadValue => new CaseDescriptor(
                    "Unary",
                    "request=empty,response=value,idempotent=no,timeout=no,cancel=no,streams=0",
                    "tiny",
                    7,
                    async () => await rpc.PingAsync().ConfigureAwait(false)),

                ClientCallShapeScenario.UnaryNoPayloadNoResponse => new CaseDescriptor(
                    "Unary",
                    "request=empty,response=none,idempotent=no,timeout=no,cancel=no,streams=0",
                    "tiny",
                    0,
                    async () =>
                    {
                        await rpc.TouchAsync().ConfigureAwait(false);
                        return 0;
                    }),

                ClientCallShapeScenario.UnaryRequiredReference => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=required-ref,idempotent=no,timeout=no,cancel=no,streams=0",
                    "tiny",
                    5,
                    async () => (await rpc.EchoAsync("shape").ConfigureAwait(false)).Length),

                ClientCallShapeScenario.UnaryNullableReference => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=nullable-ref,idempotent=no,timeout=no,cancel=no,streams=0",
                    "tiny",
                    1,
                    async () => await rpc.EchoNullableAsync(null).ConfigureAwait(false) is null ? 1 : 0),

                ClientCallShapeScenario.UnaryBytes4096 => new CaseDescriptor(
                    "Unary",
                    "request=payload,response=required-ref,idempotent=no,timeout=no,cancel=no,streams=0",
                    "4KiB",
                    BenchmarkRpcService.GetPayloadScore(payload4096),
                    async () => BenchmarkRpcService.GetPayloadScore(
                        await rpc.EchoBytesAsync(payload4096).ConfigureAwait(false))),

                ClientCallShapeScenario.OneWayPayload => OneWay(
                    environment,
                    "request=payload,response=none,timeout=no,cancel=no,streams=0",
                    "tiny",
                    () => rpc.PublishEventAsync(7, 11, "shape")),

                ClientCallShapeScenario.OneWayTimedPayload => OneWay(
                    environment,
                    "request=payload,response=none,timeout=yes,cancel=no,streams=0",
                    "tiny",
                    () => rpc.PublishTimedEventAsync(7)),

                ClientCallShapeScenario.OneWayCancellablePayload => OneWay(
                    environment,
                    "request=payload,response=none,timeout=no,cancel=yes,streams=0",
                    "tiny",
                    () => rpc.PublishCancellableEventAsync(7, token)),

                ClientCallShapeScenario.OneWayOneClientStream => OneWay(
                    environment,
                    "request=empty,response=none,timeout=no,cancel=no,streams=1",
                    "tiny",
                    () => rpc.PublishNumbersAsync(numbers)),

                ClientCallShapeScenario.OneWayTwoClientStreamsTimed => OneWay(
                    environment,
                    "request=empty,response=none,timeout=yes,cancel=no,streams=2",
                    "tiny",
                    () => rpc.PublishTwoStreamsAsync(leftNumbers, rightNumbers)),

                ClientCallShapeScenario.ClientStreamingOneStream => new CaseDescriptor(
                    "ClientStreaming",
                    "request=empty,response=value,timeout=no,cancel=no,streams=1",
                    "tiny",
                    6,
                    async () => await rpc.UploadNumbersAsync(numbers).ConfigureAwait(false)),

                ClientCallShapeScenario.ClientStreamingTwoStreams => new CaseDescriptor(
                    "ClientStreaming",
                    "request=empty,response=value,timeout=no,cancel=no,streams=2",
                    "tiny",
                    12,
                    async () => await rpc.MergeStreamsAsync(leftNumbers, rightNumbers)
                        .ConfigureAwait(false)),

                ClientCallShapeScenario.ClientStreamingCancellable => new CaseDescriptor(
                    "ClientStreaming",
                    "request=empty,response=value,timeout=no,cancel=yes,streams=1",
                    "tiny",
                    6,
                    async () => await rpc.UploadNumbersCancellableAsync(numbers, token)
                        .ConfigureAwait(false)),

                ClientCallShapeScenario.ClientStreamingPayload4096 => new CaseDescriptor(
                    "ClientStreaming",
                    "request=empty,response=value,timeout=no,cancel=no,streams=1",
                    "4KiB",
                    BenchmarkRpcService.GetPayloadScore(payload4096),
                    async () => await rpc.UploadPayloadsAsync(payloadStream).ConfigureAwait(false)),

                ClientCallShapeScenario.ServerStreamingValue => new CaseDescriptor(
                    "ServerStreaming",
                    "request=payload,response=value,timeout=no,cancel=no,streams=0",
                    "tiny",
                    3,
                    () => SumAsync(rpc.DownloadNumbersAsync(3))),

                ClientCallShapeScenario.ServerStreamingCancellable => new CaseDescriptor(
                    "ServerStreaming",
                    "request=payload,response=value,timeout=no,cancel=yes,streams=0",
                    "tiny",
                    3,
                    () => SumAsync(rpc.DownloadNumbersCancellableAsync(3, token))),

                ClientCallShapeScenario.ServerStreamingPayload4096 => new CaseDescriptor(
                    "ServerStreaming",
                    "request=payload,response=required-ref,timeout=no,cancel=no,streams=0",
                    "4KiB",
                    BenchmarkRpcService.GetPayloadScore(payload4096),
                    () => ScorePayloadsAsync(rpc.DownloadPayloadsAsync(1, 4096))),

                ClientCallShapeScenario.DuplexRequiredReference => new CaseDescriptor(
                    "DuplexStreaming",
                    "request=empty,response=required-ref,timeout=no,cancel=no,streams=1",
                    "tiny",
                    6,
                    () => ScoreStringsAsync(rpc.DuplexAsync(strings))),

                ClientCallShapeScenario.DuplexCancellable => new CaseDescriptor(
                    "DuplexStreaming",
                    "request=empty,response=required-ref,timeout=no,cancel=yes,streams=1",
                    "tiny",
                    6,
                    () => ScoreStringsAsync(rpc.DuplexCancellableAsync(cancellableStrings, token))),

                ClientCallShapeScenario.DuplexPayload4096 => new CaseDescriptor(
                    "DuplexStreaming",
                    "request=empty,response=required-ref,timeout=no,cancel=no,streams=1",
                    "4KiB",
                    BenchmarkRpcService.GetPayloadScore(payload4096),
                    () => ScorePayloadsAsync(rpc.DuplexPayloadsAsync(payloadStream))),

                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
            };

            return new ClientCallShapeCase(
                environment,
                cancellation,
                descriptor.Shape,
                descriptor.FactSummary,
                descriptor.PayloadClass,
                descriptor.Expected,
                descriptor.InvokeAsync);
        }
        catch
        {
            cancellation.Dispose();
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        await _environment.DisposeAsync().ConfigureAwait(false);
    }

    private static CaseDescriptor OneWay(
        BenchmarkEnvironment environment,
        string facts,
        string payloadClass,
        Func<ValueTask> invoke)
        => new(
            "OneWay",
            facts,
            payloadClass,
            1,
            async () =>
            {
                var target = environment.LocalService.PublishedCount + 1;
                await invoke().ConfigureAwait(false);
                await WaitUntilPublishedAsync(environment.LocalService, target)
                    .ConfigureAwait(false);
                return 1;
            });

    private static async ValueTask WaitUntilPublishedAsync(
        BenchmarkRpcService service,
        long target)
    {
        var started = Stopwatch.GetTimestamp();
        while (service.PublishedCount < target)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException(
                    $"OneWay service completed {service.PublishedCount} calls; expected {target}.");
            }

            await Task.Yield();
        }
    }

    private static async ValueTask<long> SumAsync(IAsyncEnumerable<int> values)
    {
        long sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    private static async ValueTask<long> ScoreStringsAsync(IAsyncEnumerable<string> values)
    {
        long score = 0;
        await foreach (var value in values)
            score += value.Length;
        return score;
    }

    private static async ValueTask<long> ScorePayloadsAsync(IAsyncEnumerable<byte[]> values)
    {
        long score = 0;
        await foreach (var value in values)
            score += BenchmarkRpcService.GetPayloadScore(value);
        return score;
    }

    private sealed record CaseDescriptor(
        string Shape,
        string FactSummary,
        string PayloadClass,
        long Expected,
        Func<ValueTask<long>> InvokeAsync);
}

internal sealed class ReusableAsyncEnumerable<T>(IReadOnlyList<T> values)
    : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    private int _index = -1;
    private int _active;
    private CancellationToken _cancellationToken;

    public T Current => values[_index];

    public IAsyncEnumerator<T> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
            throw new InvalidOperationException("The reusable evidence stream is already active.");

        _index = -1;
        _cancellationToken = cancellationToken;
        return this;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var next = _index + 1;
        if (next >= values.Count)
            return ValueTask.FromResult(false);

        _index = next;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _index = -1;
        _cancellationToken = default;
        Volatile.Write(ref _active, 0);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ClientCallShapeEvidenceResult
{
    public string Commit { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Shape { get; init; } = string.Empty;
    public string FactSummary { get; init; } = string.Empty;
    public string PayloadClass { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public bool ServerGc { get; init; }
    public string TieredCompilation { get; init; } = string.Empty;
    public string TieredPgo { get; init; } = string.Empty;
    public int WarmupOperations { get; init; }
    public double RequestedMeasurementSeconds { get; init; }
    public double ActualMeasurementSeconds { get; init; }
    public int Operations { get; init; }
    public double ThroughputPerSecond { get; init; }
    public double FirstCallUs { get; init; }
    public double AverageUs { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
    public double MaxUs { get; init; }
    public double CpuUsPerOperation { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int ThreadCount { get; init; }
    public long WorkingSetBytes { get; init; }
    public int ValidationFailures { get; init; }
    public bool HitOperationLimit { get; init; }
}
