using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// #737 application-shaped loop attribution. Both paths enumerate the same synchronously
/// completing async stream, encode each Int32 through the real Codec into a reusable writer,
/// and check the same live PendingCall at the same iteration boundaries. No network, flow
/// credit, timer callbacks or concurrent consumer is included in this measurement.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class ProducerProgressLoopBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _table = null!;
    private RpcRequestOperation<int> _operation = null!;
    private PooledByteBufferWriter _writer = null!;
    private IRpcCodec<int> _codec = null!;
    private TimeProvider _clock = null!;
    private RpcDeadline _deadline;
    private long _requestId;

    [Params(1, 8, 64, 1024)]
    public int Items { get; set; }

    [Params(false, true)]
    public bool HasDeadline { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _clock = TimeProvider.System;
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _table = new PendingRequestTable(
            65_536, _context.Codecs, BenchmarkPendingCallOwner.Instance, _clock);
        _codec = _context.Codecs.GetCodec<int>();
        _writer = new PooledByteBufferWriter(128);
        _deadline = HasDeadline ? RpcDeadline.Create(TimeSpan.FromHours(1), _clock) : default;
        _operation = _table.Rent(
            _codec,
            PendingCallKind.ClientStreaming,
            _deadline,
            CancellationToken.None,
            out _requestId,
            hasResponsePayload: true,
            responseNullable: false);
        if (!_table.TryGetProducerDeadline(_requestId, out var captured) ||
            captured.HasValue != _deadline.HasValue)
        {
            throw new InvalidOperationException("Could not bind the producer's deadline once.");
        }
        // BDN never times this setup; the steady-state producer loop owns a live request.
        _deadline = captured;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _table.TryComplete(_requestId, PendingCallCompletionReason.ConnectionClosed);
        try
        {
            _ = _operation.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
        _writer.Dispose();
        _table.Dispose();
        _context.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<long> A0_LockedProducerLoop()
    {
        long checksum = 0;
        await using var enumerator = CreateItems(Items).GetAsyncEnumerator();
        while (true)
        {
            if (!_table.TryAcceptProducerProgress(_requestId))
                throw new InvalidOperationException("A0 lost a live producer.");

            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                break;

            var current = enumerator.Current;
            _writer.Clear();
            _codec.Serialize(in current, _writer);
            checksum += BinaryPrimitives.ReadInt32LittleEndian(_writer.WrittenSpan);
        }

        if (!_table.TryAcceptProducerProgress(_requestId))
            throw new InvalidOperationException("A0 could not finish a live stream.");
        return checksum;
    }

    [Benchmark]
    public async ValueTask<long> A1_LockFreeProducerLoop()
    {
        long checksum = 0;
        await using var enumerator = CreateItems(Items).GetAsyncEnumerator();
        while (true)
        {
            if (!ProducerProgressBenchmarks.TryAcceptContains(
                    _table, _requestId, _deadline, _clock))
            {
                throw new InvalidOperationException("A1 lost a live producer.");
            }

            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                break;

            var current = enumerator.Current;
            _writer.Clear();
            _codec.Serialize(in current, _writer);
            checksum += BinaryPrimitives.ReadInt32LittleEndian(_writer.WrittenSpan);
        }

        if (!ProducerProgressBenchmarks.TryAcceptContains(
                _table, _requestId, _deadline, _clock))
        {
            throw new InvalidOperationException("A1 could not finish a live stream.");
        }
        return checksum;
    }

    // Same completion shape as BenchmarkEnvironment.ToStream(): Yield + completed await per item.
    // This is deliberately not a Task.Yield() or a synthetic per-item scheduler benchmark.
    private static async IAsyncEnumerable<int> CreateItems(int items)
    {
        for (var index = 0; index < items; index++)
        {
            yield return index;
            await Task.CompletedTask;
        }
    }
}

/// <summary>
/// BDN same-request producer contention control. Persistent dedicated workers are created only
/// by GlobalSetup, and the benchmark samples one synchronized batch of 16,384 checks/worker.
/// Variant selection happens before the inner hot loop; no Task.Run or worker creation is timed.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class ProducerProgressMultiProducerBenchmarks
{
    private const int ChecksPerWorker = 16_384;
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _table = null!;
    private RpcRequestOperation<int> _operation = null!;
    private TimeProvider _clock = null!;
    private RpcDeadline _deadline;
    private long _requestId;
    private Barrier _barrier = null!;
    private Thread[] _threads = null!;
    private int _variant;
    private int _stopping;
    private long _successes;
    private Exception? _workerError;

    [Params(2, 4, 8)]
    public int Workers { get; set; }

    [Params(false, true)]
    public bool HasDeadline { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _clock = TimeProvider.System;
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _table = new PendingRequestTable(
            65_536, _context.Codecs, BenchmarkPendingCallOwner.Instance, _clock);
        _deadline = HasDeadline ? RpcDeadline.Create(TimeSpan.FromHours(1), _clock) : default;
        _operation = _table.Rent(
            _context.Codecs.GetCodec<int>(),
            PendingCallKind.ClientStreaming,
            _deadline,
            CancellationToken.None,
            out _requestId,
            hasResponsePayload: true,
            responseNullable: false);
        if (!_table.TryGetProducerDeadline(_requestId, out var captured) ||
            captured.HasValue != _deadline.HasValue)
        {
            throw new InvalidOperationException("Could not bind producer deadline.");
        }
        _deadline = captured;

        _barrier = new Barrier(Workers + 1);
        _threads = new Thread[Workers];
        for (var index = 0; index < Workers; index++)
        {
            _threads[index] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "issue737-bdn-producer-" + index
            };
            _threads[index].Start();
        }
    }

    [Benchmark(Baseline = true)]
    public long A0_LockedSameRequest()
        => MeasureBatch(variant: 0);

    [Benchmark]
    public long A1_LockFreeSameRequest()
        => MeasureBatch(variant: 1);

    private long MeasureBatch(int variant)
    {
        Volatile.Write(ref _variant, variant);
        Volatile.Write(ref _successes, 0);
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();
        var error = Volatile.Read(ref _workerError);
        if (error is not null)
            throw new InvalidOperationException("Concurrent producer worker failed.", error);

        var successes = Volatile.Read(ref _successes);
        var expected = (long)Workers * ChecksPerWorker;
        if (successes != expected)
            throw new InvalidOperationException(
                $"Producer contention check accepted {successes} of {expected} live checks.");
        return successes;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _barrier.SignalAndWait();
            if (Volatile.Read(ref _stopping) != 0)
                return;

            long accepted = 0;
            try
            {
                if (Volatile.Read(ref _variant) == 0)
                {
                    for (var index = 0; index < ChecksPerWorker; index++)
                    {
                        if (_table.TryAcceptProducerProgress(_requestId))
                            accepted++;
                    }
                }
                else
                {
                    for (var index = 0; index < ChecksPerWorker; index++)
                    {
                        if (ProducerProgressBenchmarks.TryAcceptContains(
                                _table, _requestId, _deadline, _clock))
                        {
                            accepted++;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _workerError, exception, null);
            }

            Interlocked.Add(ref _successes, accepted);
            _barrier.SignalAndWait();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Volatile.Write(ref _stopping, 1);
        _barrier.SignalAndWait();
        foreach (var thread in _threads)
        {
            if (!thread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Producer-progress benchmark worker did not exit.");
        }
        _barrier.Dispose();
        _table.TryComplete(_requestId, PendingCallCompletionReason.ConnectionClosed);
        try
        {
            _ = _operation.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
        _table.Dispose();
        _context.Dispose();
    }
}
