using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// Benchmark-only Phase 0 evidence for #273. This runner intentionally does not wire any
/// decode strategy into the production request loop. It compares execution/scheduling
/// shapes around the reviewed two-phase call reservation primitive.
/// </summary>
internal static class DecodeExecutionPhase0EvidenceRunner
{
    private const uint IntegrityMagic = 0x31504353;
    private const int IntegrityTrailerBytes = sizeof(uint) + sizeof(uint);
    private static readonly DecodeStrategy[] s_strategies =
    [
        DecodeStrategy.ThreadPoolHandoff,
        DecodeStrategy.InlineProvider,
        DecodeStrategy.CooperativeQuantum,
        DecodeStrategy.PersistentExecutor
    ];
    private static readonly AdmissionMode[] s_admissionModes =
    [
        AdmissionMode.Off,
        AdmissionMode.Immediate,
        AdmissionMode.Queued
    ];
    private static readonly int[] s_concurrency = [1, 16, 128];

    internal static async Task RunAsync(string[] args)
    {
        var outputPath = GetOption(args, "--output") ??
            Path.Combine("artifacts", "performance", "current", "phase0-decode-execution.json");
        var payloadSizes = GetPayloadSizes(args);
        var compressibility = GetCompressibility(args);
        var repetitions = GetPositiveInt(args, "--repetitions", 3);
        var quantumBytes = GetPositiveInt(args, "--quantum-bytes", 64 * 1024);
        var results = new List<DecodeExecutionEvidenceResult>();
        var lifecycle = new List<DecodeLifecycleEvidenceResult>();

        foreach (var payloadSize in payloadSizes)
        {
            foreach (var compressible in compressibility)
            {
                var fixture = DecodeFixture.Create(payloadSize, compressible);
                foreach (var remoteCancellable in new[] { false, true })
                {
                    foreach (var capacityMode in new[] { CapacityMode.Available, CapacityMode.Full })
                    {
                        foreach (var admissionMode in s_admissionModes)
                        {
                            foreach (var concurrency in s_concurrency)
                            {
                                for (var repetition = 1; repetition <= repetitions; repetition++)
                                {
                                    foreach (var strategy in GetStrategyOrder(repetition))
                                    {
                                        var result = await MeasureCaseAsync(
                                            fixture,
                                            strategy,
                                            admissionMode,
                                            capacityMode,
                                            remoteCancellable,
                                            concurrency,
                                            repetition,
                                            quantumBytes);
                                        results.Add(result);
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var strategy in s_strategies)
                {
                    lifecycle.Add(await MeasureLifecycleAsync(
                        fixture,
                        strategy,
                        quantumBytes));
                }
            }
        }

        var summary = BuildSummary(results, lifecycle);
        var evidence = new DecodeExecutionEvidenceDocument(
            DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            quantumBytes,
            results,
            lifecycle,
            summary);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        Console.WriteLine($"Phase 0 decode execution evidence: {fullPath}");
        foreach (var item in summary)
        {
            Console.WriteLine(
                $"PHASE0_SUMMARY strategy={item.Strategy} qpsRatio={item.MedianQpsRatioToInline:F3} " +
                $"cpuRatio={item.MedianCpuRatioToInline:F3} p99Ratio={item.MedianP99RatioToInline:F3} " +
                $"allocBop={item.MedianAllocatedBytesPerOperation:F1} schedulerP99Us={item.MedianSchedulerP99Microseconds:F2} " +
                $"cancelObserved={item.CancelObservedProbes}/{item.CancelProbeCount} " +
                $"cancelMedianUs={(item.MedianCancelObservationMicroseconds?.ToString("F2") ?? "n/a")} " +
                $"drainMedianUs={item.MedianStopDrainMicroseconds:F2} rejectedInvariantFailures={item.RejectedInvariantFailures}");
        }
    }

    private static async Task<DecodeExecutionEvidenceResult> MeasureCaseAsync(
        DecodeFixture fixture,
        DecodeStrategy strategy,
        AdmissionMode admissionMode,
        CapacityMode capacityMode,
        bool remoteCancellable,
        int concurrency,
        int repetition,
        int quantumBytes)
    {
        await using var runtime = new DecodeCaseRuntime(
            fixture,
            strategy,
            admissionMode,
            capacityMode,
            concurrency,
            quantumBytes);

        // Warm the provider, ArrayPool buckets, ThreadPool/executor path and async state machines.
        var warmupCount = Math.Min(concurrency, 4);
        for (var index = 0; index < warmupCount; index++)
            _ = await runtime.ExecuteAsync(remoteCancellable ? runtime.NonCancelledRemoteToken : CancellationToken.None);
        runtime.ResetMetrics();

        var operations = GetOperationsPerCase(fixture.PayloadSize, concurrency);
        var latencies = new double[operations];
        var schedulerDelays = new double[operations];
        var accepted = 0;
        var rejected = 0;
        var next = -1;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();

        var workers = new Task[concurrency];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= operations)
                        return;

                    var requestStarted = Stopwatch.GetTimestamp();
                    var request = await runtime.ExecuteAsync(
                        remoteCancellable ? runtime.NonCancelledRemoteToken : CancellationToken.None);
                    latencies[index] = ElapsedMicroseconds(requestStarted);
                    schedulerDelays[index] = request.SchedulerDelayMicroseconds;
                    if (request.Accepted)
                        Interlocked.Increment(ref accepted);
                    else
                        Interlocked.Increment(ref rejected);
                }
            });
        }
        await Task.WhenAll(workers);

        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var metrics = runtime.CaptureMetrics();
        var snapshot = runtime.CaptureCapacitySnapshot();

        if (capacityMode == CapacityMode.Available)
        {
            if (rejected != 0 || snapshot.OccupiedCalls != 0)
                throw new InvalidOperationException("Available-capacity evidence unexpectedly rejected or leaked a call reservation.");
        }
        else
        {
            if (accepted != 0 || rejected != operations)
                throw new InvalidOperationException("Full-capacity evidence did not reject every request.");
            if (metrics.DecompressCalls != 0 || metrics.DecodedRentCount != 0 || metrics.RetainedRentCount != 0)
            {
                throw new InvalidOperationException(
                    "Full-capacity compressed evidence violated #244: rejection performed decode or payload retention/rent.");
            }
            if (snapshot.OccupiedCalls != 1)
                throw new InvalidOperationException("The synthetic full-capacity holder was not preserved.");
        }

        double? cancelObservationMicroseconds = null;
        bool? cancelObserved = null;
        if (remoteCancellable && capacityMode == CapacityMode.Available)
        {
            var cancel = await MeasureCancellationAsync(
                fixture,
                strategy,
                admissionMode,
                concurrency,
                quantumBytes);
            cancelObservationMicroseconds = cancel.ObservationMicroseconds;
            cancelObserved = cancel.Observed;
        }

        return new DecodeExecutionEvidenceResult(
            strategy.ToString(),
            admissionMode.ToString(),
            capacityMode.ToString(),
            remoteCancellable,
            concurrency,
            repetition,
            fixture.PayloadSize,
            fixture.Compressible,
            fixture.Compressed.Length,
            fixture.Compressed.Length / (double)fixture.PayloadSize,
            operations,
            accepted,
            rejected,
            elapsed.TotalSeconds,
            operations / elapsed.TotalSeconds,
            cpu.TotalNanoseconds / operations,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.99),
            allocated / (double)operations,
            rejected == 0 ? null : metrics.DecompressCalls / (double)rejected,
            rejected == 0 ? null : metrics.DecodedBytesRented / (double)rejected,
            metrics.PeakRetainedBytes,
            metrics.PeakDecodedBytes,
            metrics.PeakDecodeQueueDepth,
            Percentile(schedulerDelays, 0.50),
            Percentile(schedulerDelays, 0.99),
            cancelObserved,
            cancelObservationMicroseconds);
    }

    private static async Task<CancelProbeResult> MeasureCancellationAsync(
        DecodeFixture fixture,
        DecodeStrategy strategy,
        AdmissionMode admissionMode,
        int concurrency,
        int quantumBytes)
    {
        await using var runtime = new DecodeCaseRuntime(
            fixture,
            strategy,
            admissionMode,
            CapacityMode.Available,
            Math.Max(1, concurrency),
            quantumBytes);
        using var started = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var request = Task.Run(async () =>
            await runtime.ExecuteAsync(cts.Token, () => started.Set()));
        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Cancellation probe did not reach decode execution.");

        var cancellationStarted = Stopwatch.GetTimestamp();
        cts.Cancel();
        try
        {
            _ = await request;
            return new CancelProbeResult(false, null);
        }
        catch (OperationCanceledException)
        {
            return new CancelProbeResult(true, ElapsedMicroseconds(cancellationStarted));
        }
    }

    private static async Task<DecodeLifecycleEvidenceResult> MeasureLifecycleAsync(
        DecodeFixture fixture,
        DecodeStrategy strategy,
        int quantumBytes)
    {
        const int concurrency = 16;
        await using var runtime = new DecodeCaseRuntime(
            fixture,
            strategy,
            AdmissionMode.Off,
            CapacityMode.Available,
            concurrency,
            quantumBytes);

        var tasks = new Task[concurrency];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = Task.Run(async () =>
                _ = await runtime.ExecuteAsync(CancellationToken.None));
        }

        // Let the burst publish work before measuring the drain boundary.
        await Task.Yield();
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(tasks);
        await runtime.StopExecutorAsync();
        var elapsed = ElapsedMicroseconds(started);
        var snapshot = runtime.CaptureCapacitySnapshot();
        if (snapshot.OccupiedCalls != 0)
            throw new InvalidOperationException("Lifecycle probe leaked call capacity.");

        return new DecodeLifecycleEvidenceResult(
            strategy.ToString(),
            fixture.PayloadSize,
            fixture.Compressible,
            concurrency,
            elapsed);
    }

    private static IReadOnlyList<DecodeExecutionSummary> BuildSummary(
        IReadOnlyList<DecodeExecutionEvidenceResult> results,
        IReadOnlyList<DecodeLifecycleEvidenceResult> lifecycle)
    {
        var inline = new Dictionary<CaseKey, DecodeExecutionEvidenceResult>();
        foreach (var result in results)
        {
            if (result.Strategy == DecodeStrategy.InlineProvider.ToString())
                inline[new CaseKey(result)] = result;
        }

        var summaries = new List<DecodeExecutionSummary>();
        foreach (var strategy in s_strategies)
        {
            var name = strategy.ToString();
            var qpsRatios = new List<double>();
            var cpuRatios = new List<double>();
            var p99Ratios = new List<double>();
            var allocations = new List<double>();
            var schedulerP99 = new List<double>();
            var cancelLatency = new List<double>();
            var cancelProbes = 0;
            var cancelObserved = 0;
            var rejectedInvariantFailures = 0;

            foreach (var result in results)
            {
                if (result.Strategy != name)
                    continue;
                allocations.Add(result.AllocatedBytesPerOperation);
                schedulerP99.Add(result.SchedulerDelayP99Microseconds);
                if (result.CapacityMode == CapacityMode.Full.ToString() &&
                    ((result.DecompressCallsPerRejectedRequest ?? 0) != 0 ||
                     (result.DecodedBytesRentedPerRejectedRequest ?? 0) != 0))
                    rejectedInvariantFailures++;
                if (result.CancelObserved.HasValue)
                {
                    cancelProbes++;
                    if (result.CancelObserved.Value)
                    {
                        cancelObserved++;
                        if (result.CancelObservationMicroseconds.HasValue)
                            cancelLatency.Add(result.CancelObservationMicroseconds.Value);
                    }
                }
                if (result.CapacityMode != CapacityMode.Available.ToString())
                    continue;
                var baseline = inline[new CaseKey(result)];
                qpsRatios.Add(result.Qps / baseline.Qps);
                cpuRatios.Add(result.CpuNanosecondsPerOperation / baseline.CpuNanosecondsPerOperation);
                p99Ratios.Add(result.P99Microseconds / baseline.P99Microseconds);
            }

            var drain = new List<double>();
            foreach (var probe in lifecycle)
            {
                if (probe.Strategy == name)
                    drain.Add(probe.StopDrainMicroseconds);
            }

            summaries.Add(new DecodeExecutionSummary(
                name,
                Median(qpsRatios),
                Median(cpuRatios),
                Median(p99Ratios),
                Median(allocations),
                Median(schedulerP99),
                cancelObserved,
                cancelProbes,
                cancelLatency.Count == 0 ? null : Median(cancelLatency),
                Median(drain),
                rejectedInvariantFailures));
        }
        return summaries;
    }

    private static DecodeStrategy[] GetStrategyOrder(int repetition)
        => repetition % 2 == 0
            ? [DecodeStrategy.PersistentExecutor, DecodeStrategy.CooperativeQuantum, DecodeStrategy.InlineProvider, DecodeStrategy.ThreadPoolHandoff]
            : s_strategies;

    private static int GetOperationsPerCase(int payloadSize, int concurrency)
    {
        var baseline = payloadSize switch
        {
            <= 1024 => 4096,
            <= 65_536 => 768,
            _ => 96
        };
        return Math.Max(baseline, concurrency);
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
            return 0;
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var index = Math.Clamp((int)Math.Ceiling(percentile * copy.Length) - 1, 0, copy.Length - 1);
        return copy[index];
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double ElapsedMicroseconds(long started)
        => Stopwatch.GetElapsedTime(started).TotalNanoseconds / 1000d;

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private static int GetPositiveInt(string[] args, string name, int defaultValue)
    {
        var option = GetOption(args, name);
        if (option is null)
            return defaultValue;
        if (!int.TryParse(option, out var value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Expected a positive integer.");
        return value;
    }

    private static IReadOnlyList<int> GetPayloadSizes(string[] args)
    {
        var option = GetOption(args, "--payload-size");
        if (option is null || string.Equals(option, "all", StringComparison.OrdinalIgnoreCase))
            return [1024, 65_536, 1_048_576];
        if (!int.TryParse(option, out var size) || size is not (1024 or 65_536 or 1_048_576))
            throw new ArgumentOutOfRangeException(nameof(args), "Payload size must be 1024, 65536, 1048576, or all.");
        return [size];
    }

    private static IReadOnlyList<bool> GetCompressibility(string[] args)
    {
        var option = GetOption(args, "--compressibility");
        return option?.ToLowerInvariant() switch
        {
            null or "all" => [true, false],
            "high" => [true],
            "low" => [false],
            _ => throw new ArgumentOutOfRangeException(nameof(args), "Compressibility must be high, low, or all.")
        };
    }

    private enum DecodeStrategy
    {
        ThreadPoolHandoff,
        InlineProvider,
        CooperativeQuantum,
        PersistentExecutor
    }

    private enum AdmissionMode
    {
        Off,
        Immediate,
        Queued
    }

    private enum CapacityMode
    {
        Available,
        Full
    }

    private sealed class DecodeFixture
    {
        private DecodeFixture(int payloadSize, bool compressible, byte[] compressed)
        {
            PayloadSize = payloadSize;
            Compressible = compressible;
            Compressed = compressed;
        }

        internal int PayloadSize { get; }
        internal bool Compressible { get; }
        internal byte[] Compressed { get; }

        internal static DecodeFixture Create(int payloadSize, bool compressible)
        {
            var payload = new byte[payloadSize];
            if (compressible)
                Array.Fill(payload, (byte)0x2a);
            else
                new Random(42).NextBytes(payload);
            var provider = CompressionProviderBenchmarks.CreateProvider("fastest");
            var output = new ArrayBufferWriter<byte>(payloadSize * 2 + 1024);
            var result = provider.Compress(
                new ReadOnlySequence<byte>(payload),
                output,
                payloadSize * 2 + 1024);
            if (result.ConsumedBytes != payloadSize || result.WrittenBytes != output.WrittenCount)
                throw new InvalidOperationException("Compression fixture creation returned inconsistent counts.");
            return new DecodeFixture(payloadSize, compressible, output.WrittenSpan.ToArray());
        }
    }

    private sealed class DecodeCaseRuntime : IAsyncDisposable
    {
        private readonly DecodeFixture _fixture;
        private readonly DecodeStrategy _strategy;
        private readonly AdmissionMode _admissionMode;
        private readonly int _quantumBytes;
        private readonly ISharpLinkCompressionProvider _provider;
        private readonly ServerCallCapacityGovernor _governor;
        private readonly ServerCallCapacityGovernor.ServerCallReservation? _fullCapacityHolder;
        private readonly PersistentDecodeExecutor? _executor;
        private readonly CancellationTokenSource _remoteTokenSource = new();
        private readonly DecodeMetrics _metrics = new();
        private bool _executorStopped;

        internal DecodeCaseRuntime(
            DecodeFixture fixture,
            DecodeStrategy strategy,
            AdmissionMode admissionMode,
            CapacityMode capacityMode,
            int concurrency,
            int quantumBytes)
        {
            _fixture = fixture;
            _strategy = strategy;
            _admissionMode = admissionMode;
            _quantumBytes = quantumBytes;
            _provider = CompressionProviderBenchmarks.CreateProvider("fastest");
            _governor = new ServerCallCapacityGovernor(
                capacityMode == CapacityMode.Full ? 1 : Math.Max(1, concurrency));
            if (capacityMode == CapacityMode.Full)
            {
                if (!_governor.TryReserve(out _fullCapacityHolder))
                    throw new InvalidOperationException("Failed to establish the full-capacity evidence fixture.");
            }
            if (strategy == DecodeStrategy.PersistentExecutor)
            {
                _executor = new PersistentDecodeExecutor(
                    Math.Clamp(Environment.ProcessorCount, 1, 4),
                    Math.Max(32, concurrency * 2),
                    _metrics);
            }
        }

        internal CancellationToken NonCancelledRemoteToken => _remoteTokenSource.Token;

        internal async ValueTask<DecodeRequestResult> ExecuteAsync(
            CancellationToken cancellationToken,
            Action? onDecodeStart = null)
        {
            await ApplyAdmissionAsync();
            if (!_governor.TryReserve(out var reservation))
                return new DecodeRequestResult(false, 0);

            using (reservation)
            {
                var requiresRetention = _strategy is not DecodeStrategy.InlineProvider;
                using var retained = requiresRetention
                    ? RetainedPayload.Rent(_fixture.Compressed, _metrics)
                    : default;
                var compressed = requiresRetention
                    ? retained.Memory
                    : _fixture.Compressed.AsMemory();
                using var output = new PooledOutput(_fixture.PayloadSize, _metrics);

                double schedulerDelay;
                switch (_strategy)
                {
                    case DecodeStrategy.ThreadPoolHandoff:
                        schedulerDelay = await RunThreadPoolHandoffAsync(
                            _provider,
                            compressed,
                            output,
                            _fixture.PayloadSize,
                            cancellationToken,
                            onDecodeStart,
                            _metrics);
                        break;
                    case DecodeStrategy.InlineProvider:
                        onDecodeStart?.Invoke();
                        _metrics.OnDecompress();
                        ValidateProviderResult(
                            _provider.Decompress(
                                new ReadOnlySequence<byte>(compressed),
                                output,
                                _fixture.PayloadSize,
                                cancellationToken),
                            compressed.Length,
                            _fixture.PayloadSize);
                        schedulerDelay = 0;
                        break;
                    case DecodeStrategy.CooperativeQuantum:
                        onDecodeStart?.Invoke();
                        _metrics.OnDecompress();
                        schedulerDelay = await DecompressCooperativelyAsync(
                            compressed,
                            output,
                            _fixture.PayloadSize,
                            _quantumBytes,
                            cancellationToken);
                        break;
                    case DecodeStrategy.PersistentExecutor:
                        schedulerDelay = await _executor!.EnqueueAsync(
                            _provider,
                            compressed,
                            output,
                            _fixture.PayloadSize,
                            cancellationToken,
                            onDecodeStart);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                reservation.Activate();
                return new DecodeRequestResult(true, schedulerDelay);
            }
        }

        internal void ResetMetrics() => _metrics.Reset();

        internal DecodeMetricsSnapshot CaptureMetrics() => _metrics.Capture();

        internal ServerCallCapacitySnapshot CaptureCapacitySnapshot() => _governor.CaptureSnapshot();

        internal async ValueTask StopExecutorAsync()
        {
            if (_executorStopped)
                return;
            _executorStopped = true;
            if (_executor is not null)
                await _executor.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopExecutorAsync();
            _fullCapacityHolder?.Dispose();
            _remoteTokenSource.Dispose();
            _governor.AssertInvariant();
        }

        private ValueTask ApplyAdmissionAsync()
        {
            switch (_admissionMode)
            {
                case AdmissionMode.Off:
                    return ValueTask.CompletedTask;
                case AdmissionMode.Immediate:
                    Thread.SpinWait(32);
                    return ValueTask.CompletedTask;
                case AdmissionMode.Queued:
                    return YieldAdmissionAsync();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static async ValueTask YieldAdmissionAsync()
            => await Task.Yield();
    }

    private static async ValueTask<double> RunThreadPoolHandoffAsync(
        ISharpLinkCompressionProvider provider,
        ReadOnlyMemory<byte> compressed,
        PooledOutput output,
        int originalLength,
        CancellationToken cancellationToken,
        Action? onDecodeStart,
        DecodeMetrics metrics)
    {
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedAt = Stopwatch.GetTimestamp();
        metrics.OnDecodeQueued();
        var work = new DecodeWorkItem(
            provider,
            compressed,
            output,
            originalLength,
            cancellationToken,
            onDecodeStart,
            completion,
            queuedAt,
            metrics);
        if (!ThreadPool.UnsafeQueueUserWorkItem(static item => item.Run(), work, preferLocal: false))
        {
            metrics.OnDecodeDequeued();
            throw new InvalidOperationException("ThreadPool rejected Phase 0 decode work.");
        }
        return await completion.Task;
    }

    private sealed class DecodeWorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int CancelledBeforeStart = 2;
        private readonly ISharpLinkCompressionProvider _provider;
        private readonly ReadOnlyMemory<byte> _compressed;
        private readonly PooledOutput _output;
        private readonly int _originalLength;
        private readonly CancellationToken _cancellationToken;
        private readonly Action? _onDecodeStart;
        private readonly TaskCompletionSource<double> _completion;
        private readonly long _queuedAt;
        private readonly DecodeMetrics _metrics;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _state;

        internal DecodeWorkItem(
            ISharpLinkCompressionProvider provider,
            ReadOnlyMemory<byte> compressed,
            PooledOutput output,
            int originalLength,
            CancellationToken cancellationToken,
            Action? onDecodeStart,
            TaskCompletionSource<double> completion,
            long queuedAt,
            DecodeMetrics metrics)
        {
            _provider = provider;
            _compressed = compressed;
            _output = output;
            _originalLength = originalLength;
            _cancellationToken = cancellationToken;
            _onDecodeStart = onDecodeStart;
            _completion = completion;
            _queuedAt = queuedAt;
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

        internal void DisposeQueuedCancellation() => _cancellationRegistration.Dispose();

        internal void Run()
        {
            _metrics.OnDecodeDequeued();
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            var schedulerDelay = ElapsedMicroseconds(_queuedAt);
            try
            {
                // Queue-owned cancellation may complete the caller early; after worker
                // ownership wins, check the token before any provider-side CRC/decode work.
                _cancellationToken.ThrowIfCancellationRequested();
                _onDecodeStart?.Invoke();
                _metrics.OnDecompress();
                ValidateProviderResult(
                    _provider.Decompress(
                        new ReadOnlySequence<byte>(_compressed),
                        _output,
                        _originalLength,
                        _cancellationToken),
                    _compressed.Length,
                    _originalLength);
                _completion.TrySetResult(schedulerDelay);
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
            _completion.TrySetCanceled(_cancellationToken);
        }
    }

    private sealed class PersistentDecodeExecutor : IAsyncDisposable
    {
        private readonly Channel<DecodeWorkItem> _channel;
        private readonly Task[] _workers;
        private readonly DecodeMetrics _metrics;

        internal PersistentDecodeExecutor(int workers, int capacity, DecodeMetrics metrics)
        {
            _metrics = metrics;
            _channel = Channel.CreateBounded<DecodeWorkItem>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = workers == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _workers = new Task[workers];
            for (var index = 0; index < workers; index++)
                _workers[index] = Task.Run(WorkerAsync);
        }

        internal async ValueTask<double> EnqueueAsync(
            ISharpLinkCompressionProvider provider,
            ReadOnlyMemory<byte> compressed,
            PooledOutput output,
            int originalLength,
            CancellationToken cancellationToken,
            Action? onDecodeStart)
        {
            var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedAt = Stopwatch.GetTimestamp();
            var work = new DecodeWorkItem(
                provider,
                compressed,
                output,
                originalLength,
                cancellationToken,
                onDecodeStart,
                completion,
                queuedAt,
                _metrics);
            work.EnableQueuedCancellation();
            _metrics.OnDecodeQueued();
            try
            {
                await _channel.Writer.WriteAsync(work, cancellationToken);
            }
            catch
            {
                work.DisposeQueuedCancellation();
                _metrics.OnDecodeDequeued();
                throw;
            }
            return await completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            await Task.WhenAll(_workers);
        }

        private async Task WorkerAsync()
        {
            await foreach (var work in _channel.Reader.ReadAllAsync())
                work.Run();
        }
    }

    private static async ValueTask<double> DecompressCooperativelyAsync(
        ReadOnlyMemory<byte> input,
        PooledOutput output,
        int maxOutputBytes,
        int quantumBytes,
        CancellationToken cancellationToken)
    {
        if (input.Length <= IntegrityTrailerBytes)
            throw new InvalidDataException("Compressed payload integrity trailer is truncated.");
        var trailer = input.Span[^IntegrityTrailerBytes..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != IntegrityMagic)
            throw new InvalidDataException("Compressed payload integrity trailer is missing.");
        var compressedPayload = input[..^IntegrityTrailerBytes];
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(trailer[sizeof(uint)..]);
        if (Crc32Accumulator.Compute(new ReadOnlySequence<byte>(compressedPayload)) != expectedChecksum)
            throw new InvalidDataException("Compressed payload integrity checksum does not match.");

        using var decoder = new BrotliDecoder();
        var consumed = 0;
        var written = 0;
        var quantumWritten = 0;
        var schedulerDelay = 0d;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status;
            int consumedNow;
            int writtenNow;
            if (written < maxOutputBytes)
            {
                var capacity = Math.Min(8192, maxOutputBytes - written);
                var destination = output.GetSpan(capacity)[..capacity];
                status = decoder.Decompress(
                    compressedPayload.Span[consumed..],
                    destination,
                    out consumedNow,
                    out writtenNow);
                output.Advance(writtenNow);
                written += writtenNow;
                quantumWritten += writtenNow;
            }
            else
            {
                Span<byte> outputLimitProbe = stackalloc byte[1];
                status = decoder.Decompress(
                    compressedPayload.Span[consumed..],
                    outputLimitProbe,
                    out consumedNow,
                    out writtenNow);
                if (writtenNow != 0)
                    throw new SharpLinkCompressionOutputLimitException(maxOutputBytes);
            }
            consumed += consumedNow;

            switch (status)
            {
                case OperationStatus.Done:
                    if (consumed != compressedPayload.Length)
                        throw new InvalidDataException("Compressed payload contains trailing data.");
                    if (written != maxOutputBytes)
                        throw new InvalidDataException("Phase 0 cooperative decode produced an unexpected output size.");
                    return schedulerDelay;
                case OperationStatus.InvalidData:
                    throw new InvalidDataException("Brotli payload is invalid.");
                case OperationStatus.NeedMoreData when consumed == compressedPayload.Length:
                    throw new InvalidDataException("Brotli payload is truncated.");
            }
            if (consumedNow == 0 && writtenNow == 0)
                throw new InvalidDataException("Brotli decoder made no progress.");

            if (quantumWritten >= quantumBytes)
            {
                var yieldStarted = Stopwatch.GetTimestamp();
                await Task.Yield();
                schedulerDelay += ElapsedMicroseconds(yieldStarted);
                quantumWritten = 0;
            }
        }
    }

    private static void ValidateProviderResult(
        SharpLinkCompressionResult result,
        int compressedLength,
        int originalLength)
    {
        if (result.ConsumedBytes != compressedLength || result.WrittenBytes != originalLength)
            throw new InvalidOperationException("Phase 0 decode evidence returned inconsistent provider counts.");
    }

    private readonly struct RetainedPayload : IDisposable
    {
        private readonly byte[]? _buffer;
        private readonly int _length;
        private readonly DecodeMetrics? _metrics;

        private RetainedPayload(byte[] buffer, int length, DecodeMetrics metrics)
        {
            _buffer = buffer;
            _length = length;
            _metrics = metrics;
        }

        internal ReadOnlyMemory<byte> Memory
            => _buffer is null ? default : _buffer.AsMemory(0, _length);

        internal static RetainedPayload Rent(byte[] source, DecodeMetrics metrics)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(source.Length);
            source.CopyTo(buffer, 0);
            metrics.OnRetainedRent(buffer.Length);
            return new RetainedPayload(buffer, source.Length, metrics);
        }

        public void Dispose()
        {
            if (_buffer is null)
                return;
            _metrics!.OnRetainedReturn(_buffer.Length);
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }

    private sealed class PooledOutput : IBufferWriter<byte>, IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int _limit;
        private readonly DecodeMetrics _metrics;
        private int _written;

        internal PooledOutput(int limit, DecodeMetrics metrics)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(limit);
            _limit = limit;
            _metrics = metrics;
            _metrics.OnDecodedRent(_buffer.Length);
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _limit - _written)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
            => _buffer.AsMemory(_written, GetRemainingLength(sizeHint));

        public Span<byte> GetSpan(int sizeHint = 0)
            => _buffer.AsSpan(_written, GetRemainingLength(sizeHint));

        public void Dispose()
        {
            _metrics.OnDecodedReturn(_buffer.Length);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        private int GetRemainingLength(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            var remaining = _limit - _written;
            if (sizeHint > remaining)
                throw new SharpLinkCompressionOutputLimitException(_limit);
            return remaining;
        }
    }

    private sealed class DecodeMetrics
    {
        private long _decompressCalls;
        private long _decodedRentCount;
        private long _decodedBytesRented;
        private long _retainedRentCount;
        private long _retainedBytes;
        private long _peakRetainedBytes;
        private long _decodedBytes;
        private long _peakDecodedBytes;
        private long _decodeQueueDepth;
        private long _peakDecodeQueueDepth;

        internal void OnDecompress() => Interlocked.Increment(ref _decompressCalls);

        internal void OnDecodedRent(int bytes)
        {
            Interlocked.Increment(ref _decodedRentCount);
            Interlocked.Add(ref _decodedBytesRented, bytes);
            var current = Interlocked.Add(ref _decodedBytes, bytes);
            UpdatePeak(ref _peakDecodedBytes, current);
        }

        internal void OnDecodedReturn(int bytes) => Interlocked.Add(ref _decodedBytes, -bytes);

        internal void OnRetainedRent(int bytes)
        {
            Interlocked.Increment(ref _retainedRentCount);
            var current = Interlocked.Add(ref _retainedBytes, bytes);
            UpdatePeak(ref _peakRetainedBytes, current);
        }

        internal void OnRetainedReturn(int bytes) => Interlocked.Add(ref _retainedBytes, -bytes);

        internal void OnDecodeQueued()
        {
            var current = Interlocked.Increment(ref _decodeQueueDepth);
            UpdatePeak(ref _peakDecodeQueueDepth, current);
        }

        internal void OnDecodeDequeued() => Interlocked.Decrement(ref _decodeQueueDepth);

        internal void Reset()
        {
            if (Volatile.Read(ref _retainedBytes) != 0 ||
                Volatile.Read(ref _decodedBytes) != 0 ||
                Volatile.Read(ref _decodeQueueDepth) != 0)
                throw new InvalidOperationException("Cannot reset Phase 0 metrics while resources are in flight.");
            Interlocked.Exchange(ref _decompressCalls, 0);
            Interlocked.Exchange(ref _decodedRentCount, 0);
            Interlocked.Exchange(ref _decodedBytesRented, 0);
            Interlocked.Exchange(ref _retainedRentCount, 0);
            Interlocked.Exchange(ref _peakRetainedBytes, 0);
            Interlocked.Exchange(ref _peakDecodedBytes, 0);
            Interlocked.Exchange(ref _peakDecodeQueueDepth, 0);
        }

        internal DecodeMetricsSnapshot Capture()
            => new(
                Volatile.Read(ref _decompressCalls),
                Volatile.Read(ref _decodedRentCount),
                Volatile.Read(ref _decodedBytesRented),
                Volatile.Read(ref _retainedRentCount),
                Volatile.Read(ref _peakRetainedBytes),
                Volatile.Read(ref _peakDecodedBytes),
                Volatile.Read(ref _peakDecodeQueueDepth));

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

    private readonly record struct DecodeMetricsSnapshot(
        long DecompressCalls,
        long DecodedRentCount,
        long DecodedBytesRented,
        long RetainedRentCount,
        long PeakRetainedBytes,
        long PeakDecodedBytes,
        long PeakDecodeQueueDepth);

    private readonly record struct DecodeRequestResult(
        bool Accepted,
        double SchedulerDelayMicroseconds);

    private readonly record struct CancelProbeResult(
        bool Observed,
        double? ObservationMicroseconds);

    private readonly record struct CaseKey(
        string AdmissionMode,
        string CapacityMode,
        bool RemoteCancellable,
        int Concurrency,
        int Repetition,
        int PayloadSize,
        bool Compressible)
    {
        internal CaseKey(DecodeExecutionEvidenceResult result)
            : this(
                result.AdmissionMode,
                result.CapacityMode,
                result.RemoteCancellable,
                result.Concurrency,
                result.Repetition,
                result.PayloadSize,
                result.Compressible)
        {
        }
    }
}

internal sealed record DecodeExecutionEvidenceDocument(
    DateTimeOffset CapturedAtUtc,
    string Runtime,
    string OperatingSystem,
    int ProcessorCount,
    int CooperativeQuantumBytes,
    IReadOnlyList<DecodeExecutionEvidenceResult> Results,
    IReadOnlyList<DecodeLifecycleEvidenceResult> Lifecycle,
    IReadOnlyList<DecodeExecutionSummary> Summary);

internal sealed record DecodeExecutionEvidenceResult(
    string Strategy,
    string AdmissionMode,
    string CapacityMode,
    bool RemoteCancellable,
    int Concurrency,
    int Repetition,
    int PayloadSize,
    bool Compressible,
    int CompressedBytes,
    double CompressionRatio,
    int Operations,
    int Accepted,
    int Rejected,
    double ElapsedSeconds,
    double Qps,
    double CpuNanosecondsPerOperation,
    double P50Microseconds,
    double P99Microseconds,
    double AllocatedBytesPerOperation,
    double? DecompressCallsPerRejectedRequest,
    double? DecodedBytesRentedPerRejectedRequest,
    long PeakRetainedCompressedBytes,
    long PeakDecodedBytes,
    long PeakDecodeQueueDepth,
    double SchedulerDelayP50Microseconds,
    double SchedulerDelayP99Microseconds,
    bool? CancelObserved,
    double? CancelObservationMicroseconds);

internal sealed record DecodeLifecycleEvidenceResult(
    string Strategy,
    int PayloadSize,
    bool Compressible,
    int Concurrency,
    double StopDrainMicroseconds);

internal sealed record DecodeExecutionSummary(
    string Strategy,
    double MedianQpsRatioToInline,
    double MedianCpuRatioToInline,
    double MedianP99RatioToInline,
    double MedianAllocatedBytesPerOperation,
    double MedianSchedulerP99Microseconds,
    int CancelObservedProbes,
    int CancelProbeCount,
    double? MedianCancelObservationMicroseconds,
    double MedianStopDrainMicroseconds,
    int RejectedInvariantFailures);
