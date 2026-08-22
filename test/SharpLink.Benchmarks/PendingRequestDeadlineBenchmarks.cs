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
/// Measures per-call deadline bookkeeping separately from deadline scanning. Every measured invocation
/// consumes one 256-slot page. Iteration setup clears candidate page-marker state outside the timed region
/// so each batch is fresh/re-armed; on dev the reflection field is absent and setup is a no-op.
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
    private FieldInfo? _deadlinePageMarksField;
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
        _deadlinePageMarksField = typeof(PendingRequestTable)
            .GetField("_deadlinePageMarks", BindingFlags.Instance | BindingFlags.NonPublic);
        _codec = _context.Codecs.GetCodec<int>();
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _deadline = RpcDeadline.Create(
            TimeProvider.System.GetUtcNow().AddDays(1),
            TimeProvider.System);

        // Prime pools, materialize lazy storage/marker state on the candidate, and establish the long timer.
        _ = CompleteDeadlineCall();

        // Align every invocation to a fresh 256-ID page. Each benchmark consumes exactly 256 IDs.
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
    public void ResetDeadlinePageMarks()
    {
        if (_deadlinePageMarksField?.GetValue(_pending) is byte[] marks)
            Array.Clear(marks);
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
