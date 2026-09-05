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
public class DispatcherPoolAllocationBenchmarks
{
    private const int MaxRetainedDispatchers = 1_024;
    private const int TotalOperations = 131_072;
    // IterationSetup makes BenchmarkDotNet execute one benchmark invocation per iteration.
    // Keep each invocation in the steady state long enough for a meaningful timing sample.
    private const int BatchesPerInvocation = 32;
    private const int OperationsPerInvocation = TotalOperations * BatchesPerInvocation;

    private static readonly PoolItemCodec SCodec = new();

    private readonly object _commandLock = new();
    private CountdownEvent? _commandCompleted;
    private Thread[] _workers = [];
    private ExceptionDispatchInfo? _workerFailure;
    private int _command;
    private int _commandGeneration;
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

        _commandCompleted = new CountdownEvent(WorkerCount);
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
            _commandCompleted?.Dispose();
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
    /// Performs a batched steady-state sequence of rent/return cycles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IterationSetupAttribute"/> forces BenchmarkDotNet to one invocation per
    /// iteration, so the runner cannot extend a sample by invoking this method repeatedly.
    /// Repeating the same <see cref="WorkerCommand.Run"/> command here keeps the worker-held
    /// leases established by iteration setup while making each timed sample long enough to
    /// stabilize. <see cref="BenchmarkAttribute.OperationsPerInvoke"/> still normalizes time
    /// and allocations to one rent/return cycle.
    /// </para>
    /// <para>
    /// The accumulated-operation invariant intentionally fails if a runner ever invokes this
    /// method more than once per setup, rather than silently reporting a mis-normalized result.
    /// </para>
    /// </remarks>
    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int RentCompleteDisposeReturn()
    {
        for (var batch = 0; batch < BatchesPerInvocation; batch++)
        {
            ExecuteCommand(WorkerCommand.Run);

            var expectedCompleted = checked((batch + 1) * TotalOperations);
            var completedAfterBatch = Volatile.Read(ref _completedOperations);
            if (completedAfterBatch != expectedCompleted)
            {
                throw new InvalidOperationException(
                    $"Only {completedAfterBatch}/{expectedCompleted} pool operations completed in batch {batch + 1}.");
            }
        }

        var completed = Volatile.Read(ref _completedOperations);
        if (completed != OperationsPerInvocation)
        {
            throw new InvalidOperationException(
                $"Only {completed}/{OperationsPerInvocation} pool operations completed.");
        }
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

        var commandCompleted = _commandCompleted ??
            throw new InvalidOperationException("Pool benchmark was not initialized.");
        lock (_commandLock)
        {
            // Keep the 128 workers out of Barrier's simultaneous phase-transition path. The
            // generation makes a command observable even when a worker has not started waiting
            // yet, while the reusable countdown preserves one completion from every worker.
            commandCompleted.Reset(WorkerCount);
            _command = (int)command;
            _commandGeneration++;
            Monitor.PulseAll(_commandLock);
        }
        commandCompleted.Wait();

        if (throwOnFailure)
            ThrowIfWorkerFailed();
    }

    private void WorkerLoop()
    {
        PooledAsyncStreamDispatcher<PoolItem>? heldDispatcher = null;
        var observedGeneration = 0;
        while (true)
        {
            WorkerCommand command;
            lock (_commandLock)
            {
                while (observedGeneration == _commandGeneration)
                    Monitor.Wait(_commandLock);
                observedGeneration = _commandGeneration;
                command = (WorkerCommand)_command;
            }

            var shouldStop = false;
            try
            {
                switch (command)
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
            finally
            {
                // A managed worker failure must not strand the controller or the remaining
                // workers; ExecuteCommand reports the captured exception after all acknowledge.
                _commandCompleted?.Signal();
            }

            if (shouldStop)
                return;
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
