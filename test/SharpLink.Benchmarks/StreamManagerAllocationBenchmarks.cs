using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("Streams", "Allocation")]
public class StreamManagerAllocationBenchmarks
{
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

    [Benchmark(OperationsPerInvoke = 1000)]
    public int CreateThousandIdleManagers()
    {
        var unmaterialized = 0;
        for (var index = 0; index < 1000; index++)
        {
            var manager = new StreamManager();
            if (!manager.HasMaterializedRoutingState)
                unmaterialized++;
        }
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
