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
/// executor candidate. Saturation uses a minimal local fixed-capacity channel harness;
/// queued cancellation deliberately drives the exact D runtime/work-item/lease path used by
/// the comparative matrix so ownership ordering cannot diverge between the probe and D.
/// </summary>
internal static class DecodeExecutorBackpressureEvidenceRunner
{
    private const int DefaultQueueCapacity = 8;
    private const int DefaultConcurrency = 128;
    private const int DefaultOperations = 256;
    private const int DefaultQuantumBytes = 64 * 1024;

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

        var fixture = DecodeExecutionPhase0EvidenceRunner.DecodeFixture.Create(payloadSize, compressible);
        var provider = CompressionProviderBenchmarks.CreateProvider("fastest");
        var saturation = await MeasureSaturationAsync(
            provider,
            fixture.Compressed,
            payloadSize,
            workerCount,
            queueCapacity,
            concurrency,
            operations);
        var queuedCancellation = await MeasureQueuedCancellationAsync(
            fixture,
            queueCapacity);

        var result = new DecodeExecutorBackpressureEvidenceResult(
            DateTimeOffset.UtcNow,
            payloadSize,
            compressible,
            fixture.Compressed.Length,
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
            $"reservationReleased={queuedCancellation.ReservationReleasedBeforeWorkerStart} " +
            $"retainedLeaseReleased={queuedCancellation.RetainedLeaseReleasedBeforeWorkerStart} " +
            $"decodedLeaseReleased={queuedCancellation.DecodedLeaseReleasedBeforeWorkerStart} " +
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
                    await executor.EnqueueAsync(provider, compressed, payloadSize);
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
        DecodeExecutionPhase0EvidenceRunner.DecodeFixture fixture,
        int queueCapacity)
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allWorkPublished = new ManualResetEventSlim(false);
        var publishedCount = 0;
        var unexpectedCompletions = 0;
        var cancellationSources = new CancellationTokenSource[queueCapacity];
        var requests = new Task[queueCapacity];

        await using var runtime = new DecodeExecutionPhase0EvidenceRunner.DecodeCaseRuntime(
            fixture,
            DecodeExecutionPhase0EvidenceRunner.DecodeStrategy.PersistentExecutor,
            DecodeExecutionPhase0EvidenceRunner.AdmissionMode.Off,
            DecodeExecutionPhase0EvidenceRunner.CapacityMode.Available,
            queueCapacity,
            DefaultQuantumBytes,
            executorQueueCapacity: queueCapacity,
            executorWorkerGate: workerGate.Task,
            onExecutorWorkPublished: () =>
            {
                if (Interlocked.Increment(ref publishedCount) == queueCapacity)
                    allWorkPublished.Set();
            });

        try
        {
            for (var index = 0; index < requests.Length; index++)
            {
                var cancellation = new CancellationTokenSource();
                cancellationSources[index] = cancellation;
                requests[index] = Task.Run(async () =>
                {
                    try
                    {
                        _ = await runtime.ExecuteAsync(cancellation.Token);
                        Interlocked.Increment(ref unexpectedCompletions);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                    }
                });
            }

            if (!allWorkPublished.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Actual-D queued-cancellation probe did not publish every work item.");

            var beforeCancelCapacity = runtime.CaptureCapacitySnapshot();
            var beforeCancelMetrics = runtime.CaptureMetrics();
            if (beforeCancelCapacity.OccupiedCalls != queueCapacity)
                throw new InvalidOperationException("Actual-D probe did not hold every call reservation while queued.");
            if (beforeCancelMetrics.CurrentDecodeQueueDepth != queueCapacity)
                throw new InvalidOperationException("Actual-D probe did not hold every work item in the real D queue.");
            if (beforeCancelMetrics.CurrentRetainedBytes <= 0 || beforeCancelMetrics.CurrentDecodedBytes <= 0)
                throw new InvalidOperationException("Actual-D probe did not hold the real retained/decoded leases while queued.");
            if (beforeCancelMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("Actual-D probe entered provider work before workers were released.");

            var cancellationStarted = Stopwatch.GetTimestamp();
            foreach (var cancellation in cancellationSources)
                cancellation.Cancel();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            var cancellationCompletionMicroseconds =
                Stopwatch.GetElapsedTime(cancellationStarted).TotalNanoseconds / 1000d;

            var beforeWorkerCapacity = runtime.CaptureCapacitySnapshot();
            var beforeWorkerMetrics = runtime.CaptureMetrics();
            var reservationReleased = beforeWorkerCapacity.OccupiedCalls == 0;
            var retainedLeaseReleased = beforeWorkerMetrics.CurrentRetainedBytes == 0;
            var decodedLeaseReleased = beforeWorkerMetrics.CurrentDecodedBytes == 0;
            var ownershipReleased = reservationReleased && retainedLeaseReleased && decodedLeaseReleased;

            if (Volatile.Read(ref unexpectedCompletions) != 0)
                throw new InvalidOperationException("Actual-D queued-cancellation probe unexpectedly completed decode work.");
            if (!ownershipReleased)
            {
                throw new InvalidOperationException(
                    "Actual-D queued cancellation did not release reservation/retained/decoded ownership before worker service.");
            }
            if (beforeWorkerMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("Actual-D queued cancellation entered provider work before worker service.");
            if (beforeWorkerMetrics.CurrentDecodeQueueDepth != queueCapacity)
            {
                throw new InvalidOperationException(
                    "Actual-D queued cancellation dequeued work before the deterministic worker gate was released.");
            }

            workerGate.TrySetResult();
            await runtime.StopExecutorAsync();
            var afterDrainCapacity = runtime.CaptureCapacitySnapshot();
            var afterDrainMetrics = runtime.CaptureMetrics();
            if (afterDrainMetrics.DecompressCalls != 0)
            {
                throw new InvalidOperationException(
                    "A request cancelled while queued entered the actual D provider after worker release.");
            }
            if (afterDrainMetrics.SkippedCancelledWorkItems != queueCapacity)
                throw new InvalidOperationException("Actual D did not skip every queued-cancelled work item.");
            if (afterDrainMetrics.CurrentDecodeQueueDepth != 0)
                throw new InvalidOperationException("Actual-D queued-cancellation probe left work in the executor queue.");
            if (afterDrainCapacity.OccupiedCalls != 0 ||
                afterDrainMetrics.CurrentRetainedBytes != 0 ||
                afterDrainMetrics.CurrentDecodedBytes != 0)
            {
                throw new InvalidOperationException("Actual-D queued-cancellation probe leaked request ownership after drain.");
            }

            return new QueuedCancellationEvidenceResult(
                queueCapacity,
                afterDrainMetrics.DecompressCalls,
                afterDrainMetrics.SkippedCancelledWorkItems,
                ownershipReleased,
                reservationReleased,
                retainedLeaseReleased,
                decodedLeaseReleased,
                cancellationCompletionMicroseconds);
        }
        finally
        {
            workerGate.TrySetResult();
            foreach (var cancellation in cancellationSources)
                cancellation?.Dispose();
        }
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
        private readonly Channel<SaturationDecodeWorkItem> _channel;
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
            _channel = Channel.CreateBounded<SaturationDecodeWorkItem>(new BoundedChannelOptions(queueCapacity)
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
            int originalLength)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new SaturationDecodeWorkItem(
                provider,
                compressed,
                originalLength,
                completion,
                _metrics);
            var writeStarted = Stopwatch.GetTimestamp();
            var write = _channel.Writer.WriteAsync(work);
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

    private readonly record struct SaturationDecodeWorkItem(
        ISharpLinkCompressionProvider Provider,
        ReadOnlyMemory<byte> Compressed,
        int OriginalLength,
        TaskCompletionSource Completion,
        BackpressureMetrics Metrics)
    {
        internal void Run()
        {
            Metrics.OnWorkDequeued();
            try
            {
                var output = new ArrayBufferWriter<byte>(OriginalLength);
                var result = Provider.Decompress(
                    new ReadOnlySequence<byte>(Compressed),
                    output,
                    OriginalLength,
                    CancellationToken.None);
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
        private long _queuedWorkItems;

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

        internal void OnWorkEnqueued() => Interlocked.Increment(ref _queuedWorkItems);

        internal void OnWorkDequeued() => Interlocked.Decrement(ref _queuedWorkItems);

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
                Volatile.Read(ref _queuedWorkItems));
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
        long CompletedWorkItems,
        long CurrentQueuedWorkItems);

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
    bool ReservationReleasedBeforeWorkerStart,
    bool RetainedLeaseReleasedBeforeWorkerStart,
    bool DecodedLeaseReleasedBeforeWorkerStart,
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
