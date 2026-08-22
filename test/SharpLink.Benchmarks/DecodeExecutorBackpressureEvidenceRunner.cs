using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Explicit saturation and queued-cancellation probes for the Phase 0 persistent decode
/// executor candidate. Unlike the comparative A/B/C/D matrix, these probes fix queue
/// capacity independently of offered concurrency and deliberately hold workers so queue
/// ownership can be observed before provider execution begins.
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
        var saturation = await MeasureSaturationAsync(
            provider,
            compressed,
            payloadSize,
            workerCount,
            queueCapacity,
            concurrency,
            operations);
        var queuedCancellation = await MeasureQueuedCancellationAsync(
            provider,
            compressed,
            payloadSize,
            workerCount,
            queueCapacity);

        var result = new DecodeExecutorBackpressureEvidenceResult(
            DateTimeOffset.UtcNow,
            payloadSize,
            compressible,
            compressed.Length,
            workerCount,
            queueCapacity,
            concurrency,
            operations,
            saturation.ElapsedSeconds,
            saturation.Qps,
            saturation.BackpressureWaitCount,
            saturation.PeakPendingWriters,
            saturation.BackpressureWaitP50Microseconds,
            saturation.BackpressureWaitP99Microseconds,
            saturation.CompletedWorkItems,
            queuedCancellation);

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
            $"waitCount={saturation.BackpressureWaitCount} peakPendingWriters={saturation.PeakPendingWriters} " +
            $"waitP50Us={saturation.BackpressureWaitP50Microseconds:F2} " +
            $"waitP99Us={saturation.BackpressureWaitP99Microseconds:F2}");
        Console.WriteLine(
            $"PHASE0_QUEUED_CANCEL payload={payloadSize} compressible={compressible} workers={workerCount} " +
            $"queueCapacity={queueCapacity} cancelled={queuedCancellation.CancelledRequests} " +
            $"providerStarts={queuedCancellation.ProviderStarts} " +
            $"skippedBeforeProvider={queuedCancellation.SkippedBeforeProvider} " +
            $"ownershipReleasedBeforeWorkerStart={queuedCancellation.OwnershipReleasedBeforeWorkerStart} " +
            $"cancelCompletionUs={queuedCancellation.CancellationCompletionMicroseconds:F2}");
    }

    private static async Task<SaturationEvidenceResult> MeasureSaturationAsync(
        ISharpLinkCompressionProvider provider,
        ReadOnlyMemory<byte> compressed,
        int payloadSize,
        int workerCount,
        int queueCapacity,
        int concurrency,
        int operations)
    {
        using var metrics = new BackpressureMetrics(queueCapacity);
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
        if (snapshot.CurrentQueuedWorkItems != 0)
            throw new InvalidOperationException("Backpressure probe left queued work after executor drain.");

        return new SaturationEvidenceResult(
            elapsed.TotalSeconds,
            operations / elapsed.TotalSeconds,
            snapshot.BackpressureWaitCount,
            snapshot.PeakPendingWriters,
            snapshot.MedianWaitMicroseconds,
            snapshot.P99WaitMicroseconds,
            snapshot.CompletedWorkItems);
    }

    private static async Task<QueuedCancellationEvidenceResult> MeasureQueuedCancellationAsync(
        ISharpLinkCompressionProvider provider,
        ReadOnlyMemory<byte> compressed,
        int payloadSize,
        int workerCount,
        int queueCapacity)
    {
        using var metrics = new BackpressureMetrics(queueCapacity);
        await using var executor = new SaturatedDecodeExecutor(
            workerCount,
            queueCapacity,
            metrics);
        var ownershipInFlight = 0;
        var unexpectedCompletions = 0;
        var cancellationSources = new CancellationTokenSource[queueCapacity];
        var requests = new Task[queueCapacity];

        try
        {
            for (var index = 0; index < requests.Length; index++)
            {
                var cancellation = new CancellationTokenSource();
                cancellationSources[index] = cancellation;
                Interlocked.Increment(ref ownershipInFlight);
                requests[index] = Task.Run(async () =>
                {
                    try
                    {
                        await executor.EnqueueAsync(
                            provider,
                            compressed,
                            payloadSize,
                            cancellation.Token);
                        Interlocked.Increment(ref unexpectedCompletions);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        Interlocked.Decrement(ref ownershipInFlight);
                    }
                });
            }

            if (!metrics.QueueFilled.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Queued-cancellation probe did not fill the fixed executor queue.");
            var beforeCancel = metrics.Capture();
            if (beforeCancel.ProviderStartCount != 0)
                throw new InvalidOperationException("Queued-cancellation probe started provider work before worker release.");
            if (beforeCancel.CurrentQueuedWorkItems != queueCapacity)
                throw new InvalidOperationException("Queued-cancellation probe did not hold every request in queue ownership.");

            var cancellationStarted = Stopwatch.GetTimestamp();
            foreach (var cancellation in cancellationSources)
                cancellation.Cancel();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            var cancellationCompletionMicroseconds =
                Stopwatch.GetElapsedTime(cancellationStarted).TotalNanoseconds / 1000d;

            var beforeWorkerRelease = metrics.Capture();
            if (Volatile.Read(ref ownershipInFlight) != 0)
            {
                throw new InvalidOperationException(
                    "Queued cancellation did not release caller reservation/buffer ownership before worker service.");
            }
            if (Volatile.Read(ref unexpectedCompletions) != 0)
                throw new InvalidOperationException("Queued-cancellation probe unexpectedly completed decode work.");
            if (beforeWorkerRelease.ProviderStartCount != 0)
                throw new InvalidOperationException("Queued cancellation entered provider work before worker service.");
            if (beforeWorkerRelease.QueuedCancellationCount != queueCapacity)
                throw new InvalidOperationException("Queued-cancellation probe did not cancel every queued work item.");

            executor.ReleaseWorkers();
            await executor.StopAsync();
            var afterDrain = metrics.Capture();
            if (afterDrain.ProviderStartCount != 0)
            {
                throw new InvalidOperationException(
                    "A request cancelled while queued entered provider decode after worker release.");
            }
            if (afterDrain.SkippedCancelledWorkItems != queueCapacity)
                throw new InvalidOperationException("Workers did not skip every queued-cancelled work item.");
            if (afterDrain.CurrentQueuedWorkItems != 0)
                throw new InvalidOperationException("Queued-cancellation probe left cancelled work in the executor queue.");

            return new QueuedCancellationEvidenceResult(
                queueCapacity,
                afterDrain.ProviderStartCount,
                afterDrain.SkippedCancelledWorkItems,
                Volatile.Read(ref ownershipInFlight) == 0,
                cancellationCompletionMicroseconds);
        }
        finally
        {
            executor.ReleaseWorkers();
            foreach (var cancellation in cancellationSources)
                cancellation?.Dispose();
        }
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
            try
            {
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
            }
            catch
            {
                throw;
            }

            work.EnableQueuedCancellation();
            _metrics.OnWorkEnqueued();
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

    private sealed class DecodeWorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int CancelledBeforeStart = 2;
        private readonly ISharpLinkCompressionProvider _provider;
        private readonly ReadOnlyMemory<byte> _compressed;
        private readonly int _originalLength;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource _completion;
        private readonly BackpressureMetrics _metrics;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _state;

        internal DecodeWorkItem(
            ISharpLinkCompressionProvider provider,
            ReadOnlyMemory<byte> compressed,
            int originalLength,
            CancellationToken cancellationToken,
            TaskCompletionSource completion,
            BackpressureMetrics metrics)
        {
            _provider = provider;
            _compressed = compressed;
            _originalLength = originalLength;
            _cancellationToken = cancellationToken;
            _completion = completion;
            _metrics = metrics;
        }

        internal void EnableQueuedCancellation()
        {
            if (!_cancellationToken.CanBeCanceled)
                return;
            _cancellationRegistration = _cancellationToken.Register(
                static state => ((DecodeWorkItem)state!).CancelBeforeStart(),
                this);
        }

        internal void Run()
        {
            _metrics.OnWorkDequeued();
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            {
                _cancellationRegistration.Dispose();
                _metrics.OnCancelledWorkSkipped();
                return;
            }

            _cancellationRegistration.Dispose();
            try
            {
                // Cancellation that wins while queue ownership is still held completes the
                // caller before worker service. A cancellation racing after ownership transfer
                // is checked here before any provider-side CRC/decompression work begins.
                _cancellationToken.ThrowIfCancellationRequested();
                _metrics.OnProviderStarted();
                var output = new ArrayBufferWriter<byte>(_originalLength);
                var result = _provider.Decompress(
                    new ReadOnlySequence<byte>(_compressed),
                    output,
                    _originalLength,
                    _cancellationToken);
                if (result.ConsumedBytes != _compressed.Length ||
                    result.WrittenBytes != _originalLength ||
                    output.WrittenCount != _originalLength)
                {
                    throw new InvalidOperationException(
                        "Backpressure decode returned inconsistent provider counts.");
                }
                _metrics.OnWorkCompleted();
                _completion.TrySetResult();
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        private void CancelBeforeStart()
        {
            if (Interlocked.CompareExchange(ref _state, CancelledBeforeStart, Queued) != Queued)
                return;
            _metrics.OnQueuedCancellation();
            _completion.TrySetCanceled(_cancellationToken);
        }
    }

    private sealed class BackpressureMetrics : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<double> _waitMicroseconds = [];
        private readonly int _queueCapacity;
        private long _backpressureWaitCount;
        private long _pendingWriters;
        private long _peakPendingWriters;
        private long _completedWorkItems;
        private long _queuedWorkItems;
        private long _providerStartCount;
        private long _queuedCancellationCount;
        private long _skippedCancelledWorkItems;

        internal BackpressureMetrics(int queueCapacity)
        {
            _queueCapacity = queueCapacity;
        }

        internal ManualResetEventSlim BackpressureObserved { get; } = new(false);
        internal ManualResetEventSlim QueueFilled { get; } = new(false);

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

        internal void OnWorkEnqueued()
        {
            var queued = Interlocked.Increment(ref _queuedWorkItems);
            if (queued >= _queueCapacity)
                QueueFilled.Set();
        }

        internal void OnWorkDequeued() => Interlocked.Decrement(ref _queuedWorkItems);

        internal void OnProviderStarted() => Interlocked.Increment(ref _providerStartCount);

        internal void OnQueuedCancellation() => Interlocked.Increment(ref _queuedCancellationCount);

        internal void OnCancelledWorkSkipped() => Interlocked.Increment(ref _skippedCancelledWorkItems);

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
                Volatile.Read(ref _completedWorkItems),
                Volatile.Read(ref _queuedWorkItems),
                Volatile.Read(ref _providerStartCount),
                Volatile.Read(ref _queuedCancellationCount),
                Volatile.Read(ref _skippedCancelledWorkItems));
        }

        public void Dispose()
        {
            BackpressureObserved.Dispose();
            QueueFilled.Dispose();
        }

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
        long CompletedWorkItems,
        long CurrentQueuedWorkItems,
        long ProviderStartCount,
        long QueuedCancellationCount,
        long SkippedCancelledWorkItems);

    private readonly record struct SaturationEvidenceResult(
        double ElapsedSeconds,
        double Qps,
        long BackpressureWaitCount,
        long PeakPendingWriters,
        double BackpressureWaitP50Microseconds,
        double BackpressureWaitP99Microseconds,
        long CompletedWorkItems);
}

internal sealed record QueuedCancellationEvidenceResult(
    int CancelledRequests,
    long ProviderStarts,
    long SkippedBeforeProvider,
    bool OwnershipReleasedBeforeWorkerStart,
    double CancellationCompletionMicroseconds);

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
    long CompletedWorkItems,
    QueuedCancellationEvidenceResult QueuedCancellation);
