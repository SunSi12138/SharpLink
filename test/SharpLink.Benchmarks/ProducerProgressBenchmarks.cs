using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 12)]
public class ProducerProgressBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private RpcRequestOperation<int> _operation = null!;
    private PendingRequestTable.ProducerProgressLease _lease;
    private long _requestId;
    private RpcDeadline _deadline;
    private TimeProvider _timeProvider = null!;

    [Params(false, true)]
    public bool HasDeadline { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _timeProvider = TimeProvider.System;
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            _timeProvider);
        _deadline = HasDeadline
            ? RpcDeadline.Create(TimeSpan.FromHours(1), _timeProvider)
            : default;
        _operation = _pending.Rent(
            _context.Codecs.GetCodec<int>(),
            PendingCallKind.ClientStreaming,
            _deadline,
            CancellationToken.None,
            out _requestId,
            hasResponsePayload: true,
            responseNullable: false);
        if (!_pending.TryResolveProducerProgress(_requestId, out _lease, out var resolvedDeadline) ||
            resolvedDeadline.HasValue != _deadline.HasValue)
        {
            throw new InvalidOperationException("Cannot resolve the producer-progress benchmark lease.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pending.TryComplete(_requestId, PendingCallCompletionReason.ConnectionClosed);
        try
        {
            _ = _operation.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
        _pending.Dispose();
        _context.Dispose();
    }

    [Benchmark(Baseline = true)]
    public bool A0_LockedTableCheck()
        => _pending.TryAcceptProducerProgress(_requestId);

    [Benchmark]
    public bool A1_LockFreeContains()
        => TryAcceptContains(_pending, _requestId, _deadline, _timeProvider);

    [Benchmark]
    public bool A2_ResolvedSlot()
        => TryAcceptResolved(_pending, _lease, _requestId, _deadline, _timeProvider);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryAcceptContains(
        PendingRequestTable pending,
        long requestId,
        RpcDeadline deadline,
        TimeProvider timeProvider)
    {
        if (!pending.Contains(requestId))
            return false;
        if (!deadline.IsExpired(timeProvider))
            return true;

        pending.TryComplete(requestId, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryAcceptResolved(
        PendingRequestTable pending,
        PendingRequestTable.ProducerProgressLease lease,
        long requestId,
        RpcDeadline deadline,
        TimeProvider timeProvider)
    {
        if (!lease.IsActive())
            return false;
        if (!deadline.IsExpired(timeProvider))
            return true;

        pending.TryComplete(requestId, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }
}

internal static class ProducerProgressEvidenceRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static void Run(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --producer-progress-evidence <iterations-per-worker> <output-json>");
        }

        var iterations = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        var outputPath = Path.GetFullPath(args[1]);

        var workerCounts = new[] { 1, 2, 4, 8, 32 };
        var records = new List<ProducerProgressEvidenceRecord>();
        var variants = new[]
        {
            ProducerProgressVariant.A0Locked,
            ProducerProgressVariant.A1Contains,
            ProducerProgressVariant.A2Resolved
        };

        for (var hasDeadlineIndex = 0; hasDeadlineIndex < 2; hasDeadlineIndex++)
        {
            var hasDeadline = hasDeadlineIndex != 0;
            foreach (var scope in new[] { ProducerProgressScope.SameRequest, ProducerProgressScope.IndependentRequests })
            {
                foreach (var workers in workerCounts)
                {
                    for (var repetition = 0; repetition < 3; repetition++)
                    {
                        for (var order = 0; order < variants.Length; order++)
                        {
                            var variant = variants[(order + repetition) % variants.Length];
                            records.Add(RunOne(
                                variant,
                                scope,
                                workers,
                                iterations,
                                hasDeadline,
                                repetition));
                        }
                    }
                }
            }
        }

        var document = new ProducerProgressEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            TimestampUtc = DateTimeOffset.UtcNow,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            IterationsPerWorker = iterations,
            Records = records
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(document, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
    }

    private static ProducerProgressEvidenceRecord RunOne(
        ProducerProgressVariant variant,
        ProducerProgressScope scope,
        int workers,
        int iterations,
        bool hasDeadline,
        int repetition)
    {
        using var fixture = new ProducerProgressFixture(
            scope == ProducerProgressScope.SameRequest ? 1 : workers,
            hasDeadline);

        using var ready = new CountdownEvent(workers);
        using var start = new ManualResetEventSlim();
        using var done = new CountdownEvent(workers);
        Exception? failure = null;
        long successfulChecks = 0;
        var threads = new Thread[workers];

        for (var worker = 0; worker < workers; worker++)
        {
            var workerIndex = scope == ProducerProgressScope.SameRequest ? 0 : worker;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    ready.Signal();
                    start.Wait();
                    long localSuccesses = variant switch
                    {
                        ProducerProgressVariant.A0Locked =>
                            fixture.RunA0(workerIndex, iterations),
                        ProducerProgressVariant.A1Contains =>
                            fixture.RunA1(workerIndex, iterations),
                        ProducerProgressVariant.A2Resolved =>
                            fixture.RunA2(workerIndex, iterations),
                        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
                    };
                    Interlocked.Add(ref successfulChecks, localSuccesses);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
                finally
                {
                    done.Signal();
                }
            })
            {
                IsBackground = true,
                Name = $"producer-progress-{variant}-{scope}-{worker}"
            };
            threads[worker].Start();
        }

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Producer-progress workers did not become ready.");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var contentionBefore = Monitor.LockContentionCount;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        start.Set();
        if (!done.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("Producer-progress evidence run timed out.");
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var contentionAfter = Monitor.LockContentionCount;
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        for (var index = 0; index < threads.Length; index++)
            threads[index].Join();

        if (failure is not null)
            throw new InvalidOperationException("Producer-progress evidence worker failed.", failure);

        var expected = checked((long)workers * iterations);
        if (successfulChecks != expected)
        {
            throw new InvalidOperationException(
                $"Producer-progress check rejected a live request: {successfulChecks}/{expected}.");
        }

        return new ProducerProgressEvidenceRecord
        {
            Variant = variant.ToString(),
            Scope = scope.ToString(),
            Workers = workers,
            HasDeadline = hasDeadline,
            Repetition = repetition,
            Checks = expected,
            WallNanosecondsPerCheck = elapsed.TotalNanoseconds / expected,
            CpuNanosecondsPerCheck = (cpuAfter - cpuBefore).TotalNanoseconds / expected,
            LockContentions = contentionAfter - contentionBefore,
            AllocatedBytesPerCheck = (allocatedAfter - allocatedBefore) / (double)expected
        };
    }

    private enum ProducerProgressVariant
    {
        A0Locked,
        A1Contains,
        A2Resolved
    }

    private enum ProducerProgressScope
    {
        SameRequest,
        IndependentRequests
    }

    private sealed class ProducerProgressFixture : IDisposable
    {
        private readonly SharpLinkRuntimeContext _context;
        private readonly PendingRequestTable _pending;
        private readonly RpcRequestOperation<int>[] _operations;
        private readonly long[] _requestIds;
        private readonly RpcDeadline[] _deadlines;
        private readonly PendingRequestTable.ProducerProgressLease[] _leases;
        private readonly TimeProvider _timeProvider;

        internal ProducerProgressFixture(int requests, bool hasDeadline)
        {
            _timeProvider = TimeProvider.System;
            _context = new SharpLinkRuntimeContextBuilder().Build();
            _pending = new PendingRequestTable(
                65_536,
                _context.Codecs,
                BenchmarkPendingCallOwner.Instance,
                _timeProvider);
            _operations = new RpcRequestOperation<int>[requests];
            _requestIds = new long[requests];
            _deadlines = new RpcDeadline[requests];
            _leases = new PendingRequestTable.ProducerProgressLease[requests];

            var codec = _context.Codecs.GetCodec<int>();
            for (var index = 0; index < requests; index++)
            {
                var deadline = hasDeadline
                    ? RpcDeadline.Create(TimeSpan.FromHours(1), _timeProvider)
                    : default;
                _deadlines[index] = deadline;
                _operations[index] = _pending.Rent(
                    codec,
                    PendingCallKind.ClientStreaming,
                    deadline,
                    CancellationToken.None,
                    out _requestIds[index],
                    hasResponsePayload: true,
                    responseNullable: false);
                if (!_pending.TryResolveProducerProgress(
                        _requestIds[index],
                        out _leases[index],
                        out var resolvedDeadline) ||
                    resolvedDeadline.HasValue != deadline.HasValue)
                {
                    throw new InvalidOperationException("Cannot resolve producer-progress fixture.");
                }
            }
        }

        internal long RunA0(int index, int iterations)
        {
            long successful = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                if (_pending.TryAcceptProducerProgress(_requestIds[index]))
                    successful++;
            }
            return successful;
        }

        internal long RunA1(int index, int iterations)
        {
            long successful = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                if (ProducerProgressBenchmarks.TryAcceptContains(
                        _pending,
                        _requestIds[index],
                        _deadlines[index],
                        _timeProvider))
                {
                    successful++;
                }
            }
            return successful;
        }

        internal long RunA2(int index, int iterations)
        {
            long successful = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                if (ProducerProgressBenchmarks.TryAcceptResolved(
                        _pending,
                        _leases[index],
                        _requestIds[index],
                        _deadlines[index],
                        _timeProvider))
                {
                    successful++;
                }
            }
            return successful;
        }

        public void Dispose()
        {
            for (var index = 0; index < _requestIds.Length; index++)
            {
                _pending.TryComplete(
                    _requestIds[index],
                    PendingCallCompletionReason.ConnectionClosed);
                try
                {
                    _ = _operations[index].AsValueTask().GetAwaiter().GetResult();
                }
                catch (SharpLinkException)
                {
                }
            }
            _pending.Dispose();
            _context.Dispose();
        }
    }

    private sealed class ProducerProgressEvidenceDocument
    {
        public string Commit { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
        public string Runtime { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public int IterationsPerWorker { get; init; }
        public List<ProducerProgressEvidenceRecord> Records { get; init; } = [];
    }

    private sealed class ProducerProgressEvidenceRecord
    {
        public string Variant { get; init; } = string.Empty;
        public string Scope { get; init; } = string.Empty;
        public int Workers { get; init; }
        public bool HasDeadline { get; init; }
        public int Repetition { get; init; }
        public long Checks { get; init; }
        public double WallNanosecondsPerCheck { get; init; }
        public double CpuNanosecondsPerCheck { get; init; }
        public long LockContentions { get; init; }
        public double AllocatedBytesPerCheck { get; init; }
    }
}
