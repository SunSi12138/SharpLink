using System;
using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Isolates H1: allocation and contention in the process-wide dispatcher pool.
/// </summary>
/// <remarks>
/// <para>
/// The benchmark does not enumerate or dispatch any stream item. Each worker keeps one
/// active dispatcher, then repeatedly returns it and rents the next one. The initial rent
/// and final return sit in iteration setup/cleanup so the measured loop remains a steady
/// rent/return cycle while preserving the requested pool occupancy.
/// </para>
/// <para>
/// <see cref="MemoryDiagnoserAttribute"/> is only a benchmark-thread screening signal;
/// worker-thread allocation needs a whole-process trace before attributing bytes to
/// <c>ConcurrentStack</c> nodes.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public sealed class DispatcherPoolAllocationBenchmarks
{
    private const int MaxRetainedDispatchers = 1_024;
    private const int TotalOperations = 131_072;

    private static readonly PoolItemCodec SCodec = new();

    private Barrier? _barrier;
    private Thread[] _workers = [];
    private ExceptionDispatchInfo? _workerFailure;
    private int _command;
    private int _completedOperations;

    /// <summary>Number of dispatchers returned to the pool before worker-held leases are rented.</summary>
    [Params(1, MaxRetainedDispatchers)]
    public int WarmPoolSize { get; set; }

    /// <summary>Fixed worker count that races the same closed generic dispatcher pool.</summary>
    [Params(1, 8, 32, 128)]
    public int WorkerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (TotalOperations % WorkerCount != 0)
            throw new InvalidOperationException("Total operations must divide evenly across pool workers.");

        _barrier = new Barrier(WorkerCount + 1);
        _workers = new Thread[WorkerCount];
        for (var worker = 0; worker < _workers.Length; worker++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SharpLink.DispatcherPoolBenchmarkWorker"
            };
            _workers[worker] = thread;
            thread.Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            ExecuteCommand(WorkerCommand.Stop, throwOnFailure: false);
        }
        finally
        {
            foreach (var worker in _workers)
                worker.Join();
            _barrier?.Dispose();
            PooledAsyncStreamDispatcher<PoolItem>.ClearPoolForTests();
        }
    }

    [IterationSetup(Target = nameof(RentCompleteDisposeReturn))]
    public void SetupIteration()
    {
        Volatile.Write(ref _workerFailure, null);
        Interlocked.Exchange(ref _completedOperations, 0);
        PooledAsyncStreamDispatcher<PoolItem>.ClearPoolForTests();
        WarmPool(WarmPoolSize);
        ExecuteCommand(WorkerCommand.Prepare);

        var expectedRetained = Math.Max(0, WarmPoolSize - WorkerCount);
        var actualRetained = PooledAsyncStreamDispatcher<PoolItem>.RetainedCountForTests;
        if (actualRetained != expectedRetained)
        {
            throw new InvalidOperationException(
                $"Pool warm state drifted (expected {expectedRetained}, actual {actualRetained}).");
        }
    }

    [IterationCleanup(Target = nameof(RentCompleteDisposeReturn))]
    public void CleanupIteration()
    {
        ExecuteCommand(WorkerCommand.Release, throwOnFailure: false);
        PooledAsyncStreamDispatcher<PoolItem>.ClearPoolForTests();
    }

    /// <summary>
    /// Performs 131,072 total rent/return cycles. BenchmarkDotNet reports time and allocation per cycle.
    /// </summary>
    [Benchmark(OperationsPerInvoke = TotalOperations)]
    public int RentCompleteDisposeReturn()
    {
        ExecuteCommand(WorkerCommand.Run);
        var completed = Volatile.Read(ref _completedOperations);
        if (completed != TotalOperations)
            throw new InvalidOperationException($"Only {completed}/{TotalOperations} pool operations completed.");
        return completed;
    }

    private static void WarmPool(int count)
    {
        var dispatchers = new PooledAsyncStreamDispatcher<PoolItem>[count];
        for (var index = 0; index < dispatchers.Length; index++)
            dispatchers[index] = PooledAsyncStreamDispatcher<PoolItem>.Rent(default, SCodec);
        for (var index = 0; index < dispatchers.Length; index++)
            Return(dispatchers[index]);

        var retained = PooledAsyncStreamDispatcher<PoolItem>.RetainedCountForTests;
        if (retained != count)
            throw new InvalidOperationException($"Pool warm-up retained {retained}/{count} dispatchers.");
    }

    private void ExecuteCommand(WorkerCommand command, bool throwOnFailure = true)
    {
        if (throwOnFailure)
            ThrowIfWorkerFailed();

        Volatile.Write(ref _command, (int)command);
        var barrier = _barrier ?? throw new InvalidOperationException("Pool benchmark was not initialized.");
        barrier.SignalAndWait();
        barrier.SignalAndWait();

        if (throwOnFailure)
            ThrowIfWorkerFailed();
    }

    private void WorkerLoop()
    {
        PooledAsyncStreamDispatcher<PoolItem>? heldDispatcher = null;
        try
        {
            while (true)
            {
                var barrier = _barrier ?? throw new InvalidOperationException("Pool benchmark barrier is unavailable.");
                barrier.SignalAndWait();

                var shouldStop = false;
                try
                {
                    switch ((WorkerCommand)Volatile.Read(ref _command))
                    {
                        case WorkerCommand.Prepare:
                            if (heldDispatcher is not null)
                                throw new InvalidOperationException("Worker already holds a dispatcher.");
                            heldDispatcher = PooledAsyncStreamDispatcher<PoolItem>.Rent(default, SCodec);
                            break;
                        case WorkerCommand.Run:
                            for (var operation = 0; operation < TotalOperations / WorkerCount; operation++)
                            {
                                Return(heldDispatcher ?? throw new InvalidOperationException(
                                    "Worker has no dispatcher to return."));
                                heldDispatcher = PooledAsyncStreamDispatcher<PoolItem>.Rent(default, SCodec);
                            }
                            Interlocked.Add(ref _completedOperations, TotalOperations / WorkerCount);
                            break;
                        case WorkerCommand.Release:
                            if (heldDispatcher is not null)
                            {
                                Return(heldDispatcher);
                                heldDispatcher = null;
                            }
                            break;
                        case WorkerCommand.Stop:
                            if (heldDispatcher is not null)
                                Return(heldDispatcher);
                            heldDispatcher = null;
                            shouldStop = true;
                            break;
                        default:
                            throw new InvalidOperationException("Pool worker received no command.");
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(
                        ref _workerFailure,
                        ExceptionDispatchInfo.Capture(exception),
                        null);
                }

                barrier.SignalAndWait();
                if (shouldStop)
                    return;
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _workerFailure, ExceptionDispatchInfo.Capture(exception), null);
        }
    }

    private static void Return(PooledAsyncStreamDispatcher<PoolItem> dispatcher)
    {
        dispatcher.Complete(exception: null);
        dispatcher.DisposeAsync().GetAwaiter().GetResult();
    }

    private void ThrowIfWorkerFailed()
        => Volatile.Read(ref _workerFailure)?.Throw();

    private enum WorkerCommand
    {
        None,
        Prepare,
        Run,
        Release,
        Stop
    }

    private sealed class PoolItem;

    private sealed class PoolItemCodec : IRpcCodec<PoolItem>
    {
        public void Serialize(in PoolItem value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException("The dispatcher-pool benchmark never serializes an item.");

        public PoolItem Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new NotSupportedException("The dispatcher-pool benchmark never dispatches an item.");
    }
}
