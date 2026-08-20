using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("Streams", "Allocation")]
public class StreamManagerAllocationBenchmarks
{
    private const int ConcurrentStreamCount = 32;
    private Barrier _concurrentBarrier = null!;
    private Thread[] _concurrentWorkers = null!;
    private NoOpDispatcher[] _concurrentDispatchers = null!;
    private Exception?[] _concurrentFailures = null!;
    private StreamManager? _concurrentManager;
    private int _stopConcurrentWorkers;

    [GlobalSetup(Target = nameof(CreateAndComplete32StreamsConcurrently))]
    public void SetupConcurrentFirstUseWorkers()
    {
        _concurrentBarrier = new Barrier(ConcurrentStreamCount + 1);
        _concurrentWorkers = new Thread[ConcurrentStreamCount];
        _concurrentDispatchers = new NoOpDispatcher[ConcurrentStreamCount];
        _concurrentFailures = new Exception?[ConcurrentStreamCount];
        Volatile.Write(ref _stopConcurrentWorkers, 0);

        for (var index = 0; index < ConcurrentStreamCount; index++)
        {
            var workerIndex = index;
            _concurrentDispatchers[index] = new NoOpDispatcher();
            var worker = new Thread(() => RunConcurrentFirstUseWorker(workerIndex))
            {
                IsBackground = true
            };
            _concurrentWorkers[index] = worker;
            worker.Start();
        }
    }

    [GlobalCleanup(Target = nameof(CreateAndComplete32StreamsConcurrently))]
    public void CleanupConcurrentFirstUseWorkers()
    {
        Volatile.Write(ref _stopConcurrentWorkers, 1);
        _concurrentBarrier.SignalAndWait();
        foreach (var worker in _concurrentWorkers)
        {
            if (!worker.Join(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Concurrent first-use benchmark worker did not stop.");
        }
        _concurrentBarrier.Dispose();
    }

    private void RunConcurrentFirstUseWorker(int workerIndex)
    {
        while (true)
        {
            _concurrentBarrier.SignalAndWait();
            if (Volatile.Read(ref _stopConcurrentWorkers) != 0)
                return;

            try
            {
                var manager = Volatile.Read(ref _concurrentManager)
                    ?? throw new InvalidOperationException("Concurrent manager was not published.");
                manager.Register(workerIndex + 1, _concurrentDispatchers[workerIndex]);
            }
            catch (Exception exception)
            {
                _concurrentFailures[workerIndex] = exception;
            }
            finally
            {
                _concurrentBarrier.SignalAndWait();
            }
        }
    }
    [Benchmark(Baseline = true)]
    public int CreateIdleManager()
    {
        var manager = new StreamManager();
        return manager.ActiveStreamCount;
    }

    [Benchmark]
    public int CreateAndCompleteOneStream()
    {
        var manager = new StreamManager();
        manager.Register(1, new NoOpDispatcher());
        var active = manager.ActiveStreamCount;
        manager.CompleteAll(exception: null);
        return active;
    }

    [Benchmark]
    public int CreateAndComplete32Streams()
    {
        var manager = new StreamManager();
        for (var index = 0; index < 32; index++)
            manager.Register(index + 1, new NoOpDispatcher());
        var active = manager.ActiveStreamCount;
        manager.CompleteAll(exception: null);
        return active;
    }

    [Benchmark]
    public int CreateAndComplete32StreamsConcurrently()
    {
        Array.Clear(_concurrentFailures, 0, _concurrentFailures.Length);
        var manager = new StreamManager();
        Volatile.Write(ref _concurrentManager, manager);

        _concurrentBarrier.SignalAndWait();
        _concurrentBarrier.SignalAndWait();
        Volatile.Write(ref _concurrentManager, null);

        for (var index = 0; index < _concurrentFailures.Length; index++)
        {
            var failure = _concurrentFailures[index];
            if (failure is not null)
                throw new InvalidOperationException("Concurrent first-use registration failed.", failure);
        }

        var active = manager.ActiveStreamCount;
        if (active != ConcurrentStreamCount)
            throw new InvalidOperationException($"Expected {ConcurrentStreamCount} active streams, got {active}.");
        manager.CompleteAll(exception: null);
        return active;
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public int CreateAndRetainThousandIdleManagers()
    {
        var managers = new StreamManager[1000];
        var unmaterialized = 0;
        for (var index = 0; index < managers.Length; index++)
        {
            var manager = new StreamManager();
            managers[index] = manager;
            if (!manager.HasMaterializedRoutingState)
                unmaterialized++;
        }
        GC.KeepAlive(managers);
        return unmaterialized;
    }

    private sealed class NoOpDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => ValueTask.CompletedTask;

        public void Complete(bool isError, string? errorMessage)
        {
        }

        public void Complete(Exception? exception)
        {
        }
    }
}
