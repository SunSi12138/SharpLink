using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures deadline-bearing registration/completion independently from timer scanning. Every measured
/// 256-call batch starts from a fresh scheduler epoch: the existing approximate-earliest state is reset
/// on both implementations, while candidate-only scheduling/index state is reset when present. This
/// keeps the first registration's re-arm bookkeeping inside the measured batch instead of benchmarking
/// only a permanently warmed fast path. Four persistent workers exercise the same-page contention case.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 30)]
public class PendingRequestDeadlineBenchmarks
{
    private const int DeadlinePageSize = 256;
    private const int WorkerCount = 4;
    private const int CallsPerWorker = DeadlinePageSize / WorkerCount;

    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private IRpcCodec<int> _codec = null!;
    private byte[] _responsePayload = null!;
    private RpcDeadline _deadline;
    private FieldInfo _approximateEarliestDeadlineField = null!;
    private FieldInfo? _scheduledDeadlineField;
    private Barrier _workerBarrier = null!;
    private Thread[] _workers = null!;
    private Exception?[] _workerFailures = null!;
    private int _stopWorkers;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);
        _approximateEarliestDeadlineField = typeof(PendingRequestTable).GetField(
            "_approximateEarliestDeadline",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PendingRequestTable).FullName, "_approximateEarliestDeadline");
        _scheduledDeadlineField = typeof(PendingRequestTable).GetField(
            "_scheduledDeadline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _codec = _context.Codecs.GetCodec<int>();
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _deadline = RpcDeadline.Create(
            TimeProvider.System.GetUtcNow().AddDays(1),
            TimeProvider.System);

        // Prime operation/call pools, materialize lazy storage, and establish a long timer before
        // measurement. IterationSetup then rearms the scheduler state outside the timed region.
        _ = CompleteDeadlineCall();

        while (true)
        {
            var operation = _pending.Rent<int>(out var requestId);
            _ = Complete(operation, requestId);
            if ((requestId & (DeadlinePageSize - 1)) == DeadlinePageSize - 1)
                break;
        }

        _workerFailures = new Exception?[WorkerCount];
        _workerBarrier = new Barrier(WorkerCount + 1);
        _workers = new Thread[WorkerCount];
        for (var worker = 0; worker < WorkerCount; worker++)
        {
            var workerIndex = worker;
            _workers[worker] = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"pending-deadline-bench-{workerIndex}"
            };
            _workers[worker].Start();
        }
    }

    [IterationSetup]
    public void ResetDeadlineSchedulerEpoch()
    {
        _approximateEarliestDeadlineField.SetValue(_pending, long.MaxValue);
        _scheduledDeadlineField?.SetValue(_pending, long.MaxValue);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_workers is not null)
        {
            Volatile.Write(ref _stopWorkers, 1);
            _workerBarrier.SignalAndWait();
            _workerBarrier.SignalAndWait();
            foreach (var worker in _workers)
                worker.Join();
            _workerBarrier.Dispose();
        }

        _pending.Dispose();
        _context.Dispose();
    }

    [Benchmark(OperationsPerInvoke = DeadlinePageSize)]
    public int RegisterAndCompleteWithLongDeadline()
    {
        var result = 0;
        for (var call = 0; call < DeadlinePageSize; call++)
            result = CompleteDeadlineCall();
        return result;
    }

    [Benchmark(OperationsPerInvoke = DeadlinePageSize)]
    public int RegisterAndCompleteLongDeadlinesWithinOnePage()
    {
        for (var worker = 0; worker < _workerFailures.Length; worker++)
            _workerFailures[worker] = null;

        _workerBarrier.SignalAndWait();
        _workerBarrier.SignalAndWait();

        for (var worker = 0; worker < _workerFailures.Length; worker++)
        {
            if (_workerFailures[worker] is { } failure)
                throw new InvalidOperationException($"Deadline benchmark worker {worker} failed.", failure);
        }

        var remaining = _pending.ActiveCount;
        if (remaining != 0)
            throw new InvalidOperationException($"Deadline benchmark leaked {remaining} pending calls.");
        return remaining;
    }

    private void WorkerLoop(int worker)
    {
        while (true)
        {
            _workerBarrier.SignalAndWait();
            if (Volatile.Read(ref _stopWorkers) != 0)
            {
                _workerBarrier.SignalAndWait();
                return;
            }

            try
            {
                for (var call = 0; call < CallsPerWorker; call++)
                    _ = CompleteDeadlineCall();
            }
            catch (Exception exception)
            {
                _workerFailures[worker] = exception;
            }

            _workerBarrier.SignalAndWait();
        }
    }

    private int CompleteDeadlineCall()
    {
        var operation = _pending.Rent(
            _codec,
            PendingCallKind.Unary,
            _deadline,
            CancellationToken.None,
            out var requestId);
        return Complete(operation, requestId);
    }

    private int Complete(RpcRequestOperation<int> operation, long requestId)
    {
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        if (!_pending.Dispatch(requestId, ref payload))
            throw new InvalidOperationException("Deadline benchmark dispatch failed.");
        return operation.AsValueTask().GetAwaiter().GetResult();
    }
}
