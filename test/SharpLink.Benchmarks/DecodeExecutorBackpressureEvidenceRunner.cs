using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>
/// Explicit saturation probe for the Phase 0 persistent decode executor candidate.
/// Unlike the comparative A/B/C/D matrix, this probe fixes queue capacity independently
/// of offered concurrency and deliberately holds workers until bounded-channel backpressure
/// is observed.
/// </summary>
internal static class DecodeExecutorBackpressureEvidenceRunner
{
    private const int DefaultQueueCapacity = 8;
    private const int DefaultConcurrency = 128;
    private const int DefaultOperations = 256;

    internal static async Task RunAsync(string[] args)
    {
        var outputPath = GetOption(args, "--output") ??
            Path.Combine("artifacts", "performance", "current", "phase0-decode-backpressure.json");
        var payloadSize = GetPayloadSize(args);
        var compressible = GetCompressibility(args);
        var queueCapacity = GetPositiveInt(args, "--queue-capacity", DefaultQueueCapacity);
        var concurrency = GetPositiveInt(args, "--concurrency", DefaultConcurrency);
        var operations = GetPositiveInt(args, "--operations", DefaultOperations);
        var workerCount = Math.Clamp(Environment.ProcessorCount, 1, 4);
        if (concurrency <= queueCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Backpressure evidence requires concurrency greater than queue capacity.");
        }

        var provider = CompressionProviderBenchmarks.CreateProvider("fastest");
        var compressed = CreateCompressedFixture(provider, payloadSize, compressible);
        using var metrics = new BackpressureMetrics();
        await using var executor = new SaturatedDecodeExecutor(
            workerCount,
            queueCapacity,
            metrics);

        var producerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = -1;
        var producers = new Task[concurrency];
        var started = Stopwatch.GetTimestamp();
        for (var producer = 0; producer < producers.Length; producer++)
        {
            producers[producer] = Task.Run(async () =>
            {
                await producerGate.Task;
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= operations)
                        return;
                    await executor.EnqueueAsync(
                        provider,
                        compressed,
                        payloadSize,
                        CancellationToken.None);
                }
            });
        }

        producerGate.TrySetResult();
        if (!metrics.BackpressureObserved.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(
                "Fixed-capacity Phase 0 executor did not exercise bounded-channel backpressure.");
        }

        executor.ReleaseWorkers();
        await Task.WhenAll(producers);
        await executor.StopAsync();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var snapshot = metrics.Capture();
        if (snapshot.BackpressureWaitCount == 0 || snapshot.PeakPendingWriters == 0)
            throw new InvalidOperationException("Backpressure metrics did not record a blocked writer.");
        if (snapshot.CompletedWorkItems != operations)
            throw new InvalidOperationException("Backpressure probe did not complete every submitted decode.");

        var result = new DecodeExecutorBackpressureEvidenceResult(
            DateTimeOffset.UtcNow,
            payloadSize,
            compressible,
            compressed.Length,
            workerCount,
            queueCapacity,
            concurrency,
            operations,
            elapsed.TotalSeconds,
            operations / elapsed.TotalSeconds,
            snapshot.BackpressureWaitCount,
            snapshot.PeakPendingWriters,
            snapshot.MedianWaitMicroseconds,
            snapshot.P99WaitMicroseconds,
            snapshot.CompletedWorkItems);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        Console.WriteLine($"Phase 0 decode executor backpressure evidence: {fullPath}");
        Console.WriteLine(
            $"PHASE0_BACKPRESSURE payload={payloadSize} compressible={compressible} workers={workerCount} " +
            $"queueCapacity={queueCapacity} concurrency={concurrency} operations={operations} " +
            $"waitCount={snapshot.BackpressureWaitCount} peakPendingWriters={snapshot.PeakPendingWriters} " +
            $"waitP50Us={snapshot.MedianWaitMicroseconds:F2} waitP99Us={snapshot.P99WaitMicroseconds:F2}");
    }

    private static byte[] CreateCompressedFixture(
        ISharpLinkCompressionProvider provider,
        int payloadSize,
        bool compressible)
    {
        var payload = new byte[payloadSize];
        if (compressible)
            Array.Fill(payload, (byte)0x2a);
        else
            new Random(42).NextBytes(payload);
        var output = new ArrayBufferWriter<byte>(payloadSize * 2 + 1024);
        var result = provider.Compress(
            new ReadOnlySequence<byte>(payload),
            output,
            payloadSize * 2 + 1024);
        if (result.ConsumedBytes != payloadSize || result.WrittenBytes != output.WrittenCount)
            throw new InvalidOperationException("Backpressure fixture compression returned inconsistent counts.");
        return output.WrittenSpan.ToArray();
    }

    private static int GetPayloadSize(string[] args)
    {
        var option = GetOption(args, "--payload-size");
        if (!int.TryParse(option, out var payloadSize) ||
            payloadSize is not (1024 or 65_536 or 1_048_576))
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Payload size must be 1024, 65536, or 1048576.");
        }
        return payloadSize;
    }

    private static bool GetCompressibility(string[] args)
        => GetOption(args, "--compressibility")?.ToLowerInvariant() switch
        {
            "high" => true,
            "low" => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(args),
                "Compressibility must be high or low.")
        };

    private static int GetPositiveInt(string[] args, string name, int defaultValue)
    {
        var option = GetOption(args, name);
        if (option is null)
            return defaultValue;
        if (!int.TryParse(option, out var value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Expected a positive integer.");
        return value;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private sealed class SaturatedDecodeExecutor : IAsyncDisposable
    {
        private readonly Channel<DecodeWorkItem> _channel;
        private readonly Task[] _workers;
        private readonly TaskCompletionSource _workerGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BackpressureMetrics _metrics;
        private bool _stopped;

        internal SaturatedDecodeExecutor(
            int workerCount,
            int queueCapacity,
            BackpressureMetrics metrics)
        {
            _metrics = metrics;
            _channel = Channel.CreateBounded<DecodeWorkItem>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = workerCount == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _workers = new Task[workerCount];
            for (var index = 0; index < _workers.Length; index++)
                _workers[index] = Task.Run(WorkerAsync);
        }

        internal async ValueTask EnqueueAsync(
            ISharpLinkCompressionProvider provider,
            ReadOnlyMemory<byte> compressed,
            int originalLength,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new DecodeWorkItem(
                provider,
                compressed,
                originalLength,
                cancellationToken,
                completion,
                _metrics);
            var writeStarted = Stopwatch.GetTimestamp();
            var write = _channel.Writer.WriteAsync(work, cancellationToken);
            if (!write.IsCompletedSuccessfully)
            {
                _metrics.OnBackpressureWaitStarted();
                try
                {
                    await write;
                }
                finally
                {
                    _metrics.OnBackpressureWaitCompleted(
                        Stopwatch.GetElapsedTime(writeStarted).TotalNanoseconds / 1000d);
                }
            }
            else
            {
                await write;
            }
            await completion.Task;
        }

        internal void ReleaseWorkers() => _workerGate.TrySetResult();

        internal async ValueTask StopAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            ReleaseWorkers();
            _channel.Writer.TryComplete();
            await Task.WhenAll(_workers);
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task WorkerAsync()
        {
            await _workerGate.Task;
            await foreach (var work in _channel.Reader.ReadAllAsync())
                work.Run();
        }
    }

    private readonly record struct DecodeWorkItem(
        ISharpLinkCompressionProvider Provider,
        ReadOnlyMemory<byte> Compressed,
        int OriginalLength,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion,
        BackpressureMetrics Metrics)
    {
        internal void Run()
        {
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                var output = new ArrayBufferWriter<byte>(OriginalLength);
                var result = Provider.Decompress(
                    new ReadOnlySequence<byte>(Compressed),
                    output,
                    OriginalLength,
                    CancellationToken);
                if (result.ConsumedBytes != Compressed.Length ||
                    result.WrittenBytes != OriginalLength ||
                    output.WrittenCount != OriginalLength)
                {
                    throw new InvalidOperationException(
                        "Backpressure decode returned inconsistent provider counts.");
                }
                Metrics.OnWorkCompleted();
                Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }
    }

    private sealed class BackpressureMetrics : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<double> _waitMicroseconds = [];
        private long _backpressureWaitCount;
        private long _pendingWriters;
        private long _peakPendingWriters;
        private long _completedWorkItems;

        internal ManualResetEventSlim BackpressureObserved { get; } = new(false);

        internal void OnBackpressureWaitStarted()
        {
            Interlocked.Increment(ref _backpressureWaitCount);
            var pending = Interlocked.Increment(ref _pendingWriters);
            UpdatePeak(ref _peakPendingWriters, pending);
            BackpressureObserved.Set();
        }

        internal void OnBackpressureWaitCompleted(double microseconds)
        {
            Interlocked.Decrement(ref _pendingWriters);
            lock (_gate)
                _waitMicroseconds.Add(microseconds);
        }

        internal void OnWorkCompleted() => Interlocked.Increment(ref _completedWorkItems);

        internal BackpressureMetricsSnapshot Capture()
        {
            double[] waits;
            lock (_gate)
                waits = [.. _waitMicroseconds];
            Array.Sort(waits);
            return new BackpressureMetricsSnapshot(
                Volatile.Read(ref _backpressureWaitCount),
                Volatile.Read(ref _peakPendingWriters),
                Percentile(waits, 0.50),
                Percentile(waits, 0.99),
                Volatile.Read(ref _completedWorkItems));
        }

        public void Dispose() => BackpressureObserved.Dispose();

        private static double Percentile(double[] values, double percentile)
        {
            if (values.Length == 0)
                return 0;
            var index = Math.Clamp(
                (int)Math.Ceiling(percentile * values.Length) - 1,
                0,
                values.Length - 1);
            return values[index];
        }

        private static void UpdatePeak(ref long target, long value)
        {
            while (true)
            {
                var observed = Volatile.Read(ref target);
                if (observed >= value)
                    return;
                if (Interlocked.CompareExchange(ref target, value, observed) == observed)
                    return;
            }
        }
    }

    private readonly record struct BackpressureMetricsSnapshot(
        long BackpressureWaitCount,
        long PeakPendingWriters,
        double MedianWaitMicroseconds,
        double P99WaitMicroseconds,
        long CompletedWorkItems);
}

internal sealed record DecodeExecutorBackpressureEvidenceResult(
    DateTimeOffset CapturedAtUtc,
    int PayloadSize,
    bool Compressible,
    int CompressedBytes,
    int WorkerCount,
    int QueueCapacity,
    int Concurrency,
    int Operations,
    double ElapsedSeconds,
    double Qps,
    long BackpressureWaitCount,
    long PeakPendingWriters,
    double BackpressureWaitP50Microseconds,
    double BackpressureWaitP99Microseconds,
    long CompletedWorkItems);
