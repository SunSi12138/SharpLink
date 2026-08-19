using System;
using System.Buffers;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("PendingRequestTable", "Contention")]
public class PendingRequestContentionBenchmarks
{
    // Keep the total operation count constant across producer counts so BenchmarkDotNet reports
    // comparable per-register/complete costs for the dev-vs-head contention gate.
    private const int OperationsPerInvocation = 16_384;
    private static int s_lifecycleState;
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private IPendingCallOwner _owner = null!;
    private Barrier _phase = null!;
    private Thread[] _workers = null!;
    private byte[] _responsePayload = null!;
    private int _operationsPerWorker;
    private int _stop;
    private Exception? _workerFailure;

    [Params(1, 8, 32, 128)]
    public int Producers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (OperationsPerInvocation % Producers != 0)
            throw new InvalidOperationException("Operations must divide evenly across producers.");

        _context = new SharpLinkRuntimeContextBuilder().Build();

        // The dev baseline counts pending calls in ClientConnection._activeCallCount and runs its
        // draining-idle fast return when that count reaches zero. The optimized head reuses the
        // table count and receives the same lifecycle notification from the table. Detect the
        // revision once during setup; no revision branch runs inside a timed operation.
        var tableOwnsLifecycleCount = typeof(PendingRequestTable).GetProperty(
            "ActiveCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is not null;
        _owner = tableOwnsLifecycleCount
            ? NoopLifecycleOwner.Instance
            : new CountingLifecycleOwner();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            _owner,
            TimeProvider.System);
        _responsePayload = new byte[sizeof(int)];
        _operationsPerWorker = OperationsPerInvocation / Producers;
        _phase = new Barrier(Producers + 1);
        _workers = new Thread[Producers];
        for (var index = 0; index < _workers.Length; index++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"pending-bdn-{index}"
            };
            _workers[index] = worker;
            worker.Start();
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int ConcurrentRegisterAndComplete()
    {
        ThrowWorkerFailure();
        _phase.SignalAndWait();
        _phase.SignalAndWait();
        ThrowWorkerFailure();
        return OperationsPerInvocation;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Volatile.Write(ref _stop, 1);
        if (_workers is { Length: > 0 } && _phase is not null)
            _phase.SignalAndWait();

        foreach (var worker in _workers ?? Array.Empty<Thread>())
            worker.Join();

        if (_owner is CountingLifecycleOwner countingOwner && countingOwner.ActiveCount != 0)
            throw new InvalidOperationException("Benchmark lifecycle accounting did not return to zero.");

        _phase?.Dispose();
        _pending?.Dispose();
        _context?.Dispose();
    }

    private void WorkerLoop()
    {
        var participantRemoved = false;
        try
        {
            while (true)
            {
                _phase.SignalAndWait();
                if (Volatile.Read(ref _stop) != 0)
                    return;

                for (var index = 0; index < _operationsPerWorker; index++)
                {
                    var operation = _pending.Rent<int>(out var requestId);
                    var payload = new ReadOnlySequence<byte>(_responsePayload);
                    if (!_pending.Dispatch(requestId, ref payload))
                        throw new InvalidOperationException("Benchmark response did not match its pending request.");
                    _ = operation.AsValueTask().GetAwaiter().GetResult();
                }

                _phase.SignalAndWait();
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _workerFailure, exception, null);
            try
            {
                _phase.RemoveParticipant();
                participantRemoved = true;
            }
            catch
            {
            }
        }
        finally
        {
            if (!participantRemoved && Volatile.Read(ref _stop) != 0)
            {
                try
                {
                    _phase.RemoveParticipant();
                }
                catch
                {
                }
            }
        }
    }

    private void ThrowWorkerFailure()
    {
        if (Volatile.Read(ref _workerFailure) is { } failure)
            throw new InvalidOperationException("A contention benchmark worker failed.", failure);
    }

    private static void ObserveLifecycleIdle()
    {
        // ClientConnection.RetireDrainingConnectionIfIdle returns immediately for a Ready
        // connection after reading its lifecycle state. Keep that zero-count work equivalent
        // across revisions without introducing locks, allocation, or revision-specific branches.
        if (Volatile.Read(ref s_lifecycleState) != 0)
            throw new InvalidOperationException("Unexpected benchmark lifecycle state.");
    }

    private sealed class CountingLifecycleOwner : IPendingCallOwner
    {
        private int _activeCount;

        public int ActiveCount => Volatile.Read(ref _activeCount);

        public void OnPendingCallRegistered()
            => Interlocked.Increment(ref _activeCount);

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            var remaining = Interlocked.Decrement(ref _activeCount);
            if (remaining < 0)
                throw new InvalidOperationException("Benchmark lifecycle accounting underflowed.");
            if (remaining == 0)
                ObserveLifecycleIdle();
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        // On optimized heads the table owns the pending count and calls this hook. On dev this is
        // simply an extra public method and is never invoked because the older interface lacks it.
        public void OnPendingCallCapacityIdle()
            => ObserveLifecycleIdle();
    }

    private sealed class NoopLifecycleOwner : IPendingCallOwner
    {
        internal static NoopLifecycleOwner Instance { get; } = new();

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        public void OnPendingCallCapacityIdle()
            => ObserveLifecycleIdle();
    }
}
