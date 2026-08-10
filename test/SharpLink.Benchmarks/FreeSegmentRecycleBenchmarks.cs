using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>Isolates H2 for value-type stream items and the dispatcher free-segment stack.</summary>
/// <remarks>
/// The steady-state cases retain one dispatcher lease for the entire measured operation.
/// No dispatcher is returned to its process-wide pool until iteration cleanup. The producer/consumer
/// case uses one fixed producer thread and the BenchmarkDotNet worker as the consumer, so segment
/// recycle crosses the same thread boundary as a normal streaming dispatcher. Profile both threads:
/// the memory diagnoser alone does not attribute producer-thread allocation.
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class FreeSegmentRecycleValueBenchmarks
{
    private const int SteadyStateItemCount = 131_072;
    private const int SteadyStateBatchSize = 256;
    private const int InterleavedBatchSize = 16;

    private static readonly ByteCodec SCodec = new();
    private readonly SegmentRecycleScenario<byte> _scenario = new(SCodec);

    [GlobalSetup]
    public void Setup() => _scenario.Start();

    [GlobalCleanup]
    public void Cleanup() => _scenario.Stop();

    [IterationSetup(Target = nameof(SingleThreadSteadyState_256))]
    public void SetupSingleThreadSteadyState() => _scenario.PrepareSingleThreadSteadyState(SteadyStateBatchSize);

    [IterationCleanup(Target = nameof(SingleThreadSteadyState_256))]
    public void CleanupSingleThreadSteadyState() => _scenario.DisposeLease();

    [Benchmark(OperationsPerInvoke = SteadyStateItemCount)]
    public int SingleThreadSteadyState_256()
        => _scenario.RunSingleThreadCycles(SteadyStateItemCount, SteadyStateBatchSize);

    [IterationSetup(Target = nameof(ProducerConsumerInterleave_16))]
    public void SetupProducerConsumerInterleave() => _scenario.PrepareCrossThreadSteadyState(InterleavedBatchSize);

    [IterationCleanup(Target = nameof(ProducerConsumerInterleave_16))]
    public void CleanupProducerConsumerInterleave() => _scenario.DisposeLease();

    [Benchmark(OperationsPerInvoke = SteadyStateItemCount)]
    public int ProducerConsumerInterleave_16()
        => _scenario.RunCrossThreadCycles(SteadyStateItemCount, InterleavedBatchSize);

    [IterationSetup(Target = nameof(GrowControl_256))]
    public void SetupGrowControl() => _scenario.PrepareGrowControl();

    [IterationCleanup(Target = nameof(GrowControl_256))]
    public void CleanupGrowControl() => _scenario.DisposeLease();

    /// <summary>
    /// Intentionally starts from a fresh 16-element segment. Its array allocations are a control,
    /// not evidence of free-stack node allocation.
    /// </summary>
    [Benchmark(OperationsPerInvoke = SteadyStateBatchSize)]
    public int GrowControl_256() => _scenario.RunGrowControl(SteadyStateBatchSize);

    private sealed class ByteCodec : IRpcCodec<byte>
    {
        public void Serialize(in byte value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(1);
            span[0] = value;
            buffer.Advance(1);
        }

        public byte Deserialize(in ReadOnlySequence<byte> buffer) => buffer.FirstSpan[0];
    }
}

/// <summary>Repeats the H2 measurement with a reusable reference item to expose slot-clearing costs.</summary>
[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class FreeSegmentRecycleReferenceBenchmarks
{
    private const int SteadyStateItemCount = 131_072;
    private const int SteadyStateBatchSize = 256;
    private const int InterleavedBatchSize = 16;

    private static readonly ReferenceItemCodec SCodec = new();
    private readonly SegmentRecycleScenario<ReferenceItem> _scenario = new(SCodec);

    [GlobalSetup]
    public void Setup() => _scenario.Start();

    [GlobalCleanup]
    public void Cleanup() => _scenario.Stop();

    [IterationSetup(Target = nameof(SingleThreadSteadyState_256))]
    public void SetupSingleThreadSteadyState() => _scenario.PrepareSingleThreadSteadyState(SteadyStateBatchSize);

    [IterationCleanup(Target = nameof(SingleThreadSteadyState_256))]
    public void CleanupSingleThreadSteadyState() => _scenario.DisposeLease();

    [Benchmark(OperationsPerInvoke = SteadyStateItemCount)]
    public int SingleThreadSteadyState_256()
        => _scenario.RunSingleThreadCycles(SteadyStateItemCount, SteadyStateBatchSize);

    [IterationSetup(Target = nameof(ProducerConsumerInterleave_16))]
    public void SetupProducerConsumerInterleave() => _scenario.PrepareCrossThreadSteadyState(InterleavedBatchSize);

    [IterationCleanup(Target = nameof(ProducerConsumerInterleave_16))]
    public void CleanupProducerConsumerInterleave() => _scenario.DisposeLease();

    [Benchmark(OperationsPerInvoke = SteadyStateItemCount)]
    public int ProducerConsumerInterleave_16()
        => _scenario.RunCrossThreadCycles(SteadyStateItemCount, InterleavedBatchSize);

    [IterationSetup(Target = nameof(GrowControl_256))]
    public void SetupGrowControl() => _scenario.PrepareGrowControl();

    [IterationCleanup(Target = nameof(GrowControl_256))]
    public void CleanupGrowControl() => _scenario.DisposeLease();

    [Benchmark(OperationsPerInvoke = SteadyStateBatchSize)]
    public int GrowControl_256() => _scenario.RunGrowControl(SteadyStateBatchSize);

    private sealed class ReferenceItem;

    private sealed class ReferenceItemCodec : IRpcCodec<ReferenceItem>
    {
        private static readonly ReferenceItem SItem = new();

        public void Serialize(in ReferenceItem value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(1);
            span[0] = 1;
            buffer.Advance(1);
        }

        public ReferenceItem Deserialize(in ReadOnlySequence<byte> buffer) => SItem;
    }
}

/// <summary>
/// Holds one dispatcher lease while exercising its segment chain. It deliberately uses the existing
/// concurrent free-segment stack: <c>StreamManager.DispatchChunkAsync</c> admits concurrent dispatch
/// acquisition, so the benchmark must not assume a thread-confined producer list.
/// </summary>
internal sealed class SegmentRecycleScenario<T>
{
    private const int InitialCapacity = 16;
    private const int StablePassesRequired = 2;
    private const int MaximumWarmPasses = 32;

    private static readonly ReadOnlySequence<byte> SPayload = new(new byte[] { 1 });

    private readonly IRpcCodec<T> _codec;
    private readonly Barrier _producerBarrier = new(2);
    private Thread? _producerThread;
    private PooledAsyncStreamDispatcher<T>? _dispatcher;
    private IAsyncEnumerator<T>? _enumerator;
    private ExceptionDispatchInfo? _producerFailure;
    private int _requestedItemCount;
    private int _stopProducer;

    public SegmentRecycleScenario(IRpcCodec<T> codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public void Start()
    {
        if (_producerThread is not null)
            throw new InvalidOperationException("The recycle producer is already running.");

        Volatile.Write(ref _stopProducer, 0);
        Volatile.Write(ref _producerFailure, null);
        _producerThread = new Thread(ProducerLoop)
        {
            IsBackground = true,
            Name = "SharpLink.FreeSegmentBenchmarkProducer"
        };
        _producerThread.Start();
    }

    public void Stop()
    {
        try
        {
            DisposeLease();
            if (_producerThread is not null)
            {
                Volatile.Write(ref _stopProducer, 1);
                _producerBarrier.SignalAndWait();
                _producerBarrier.SignalAndWait();
                _producerThread.Join();
                _producerThread = null;
            }
        }
        finally
        {
            _producerBarrier.Dispose();
            PooledAsyncStreamDispatcher<T>.ClearPoolForTests();
        }
    }

    public void PrepareSingleThreadSteadyState(int batchSize)
    {
        PrepareFreshLease();
        WarmUntilCapacityStopsGrowing(batchSize, useProducerThread: false);
    }

    public void PrepareCrossThreadSteadyState(int batchSize)
    {
        PrepareFreshLease();
        WarmUntilCapacityStopsGrowing(batchSize, useProducerThread: true);
    }

    public void PrepareGrowControl()
    {
        PrepareFreshLease();
        var capacity = (_dispatcher ?? throw new InvalidOperationException("Dispatcher was not prepared."))
            .BufferCapacityForTests;
        if (capacity != InitialCapacity)
            throw new InvalidOperationException($"Grow control began at capacity {capacity}, not {InitialCapacity}.");
    }

    public int RunSingleThreadCycles(int itemCount, int batchSize)
    {
        ThrowIfProducerFailed();
        var consumed = 0;
        for (var remaining = itemCount; remaining > 0;)
        {
            var batchCount = Math.Min(batchSize, remaining);
            ProduceOnCurrentThread(batchCount);
            consumed += ConsumeExactly(batchCount);
            remaining -= batchCount;
        }

        return consumed;
    }

    public int RunCrossThreadCycles(int itemCount, int batchSize)
    {
        ThrowIfProducerFailed();
        var consumed = 0;
        for (var remaining = itemCount; remaining > 0;)
        {
            var batchCount = Math.Min(batchSize, remaining);
            RequestProducerBatch(batchCount);
            consumed += ConsumeExactly(batchCount);
            remaining -= batchCount;
        }

        return consumed;
    }

    public int RunGrowControl(int itemCount)
    {
        var consumed = RunSingleThreadCycles(itemCount, itemCount);
        var capacity = (_dispatcher ?? throw new InvalidOperationException("Dispatcher was not prepared."))
            .BufferCapacityForTests;
        if (capacity <= InitialCapacity)
            throw new InvalidOperationException("The grow control did not allocate a larger segment chain.");
        return consumed;
    }

    public void DisposeLease()
    {
        var dispatcher = _dispatcher;
        var enumerator = _enumerator;
        _dispatcher = null;
        _enumerator = null;
        if (dispatcher is null)
            return;

        dispatcher.Complete(exception: null);
        (enumerator ?? throw new InvalidOperationException("Dispatcher has no enumerator."))
            .DisposeAsync()
            .GetAwaiter()
            .GetResult();
    }

    private void PrepareFreshLease()
    {
        ThrowIfProducerFailed();
        if (_dispatcher is not null || _enumerator is not null)
            throw new InvalidOperationException("The previous recycle lease was not disposed.");

        PooledAsyncStreamDispatcher<T>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<T>.Rent(default, _codec);
        _dispatcher = dispatcher;
        _enumerator = dispatcher.GetAsyncEnumerator();
    }

    private void WarmUntilCapacityStopsGrowing(int batchSize, bool useProducerThread)
    {
        var previousCapacity = -1;
        var stablePasses = 0;
        for (var pass = 0; pass < MaximumWarmPasses; pass++)
        {
            if (useProducerThread)
                _ = RunCrossThreadCycles(batchSize, batchSize);
            else
                _ = RunSingleThreadCycles(batchSize, batchSize);

            var capacity = (_dispatcher ?? throw new InvalidOperationException("Dispatcher was not prepared."))
                .BufferCapacityForTests;
            stablePasses = capacity == previousCapacity ? stablePasses + 1 : 0;
            if (stablePasses >= StablePassesRequired)
                return;
            previousCapacity = capacity;
        }

        throw new InvalidOperationException("Free-segment warm-up did not reach a stable capacity.");
    }

    private void ProduceOnCurrentThread(int itemCount)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("Dispatcher was not prepared.");
        for (var index = 0; index < itemCount; index++)
            dispatcher.DispatchAsync(SPayload, encodedByteCount: 1).GetAwaiter().GetResult();
    }

    private void RequestProducerBatch(int itemCount)
    {
        ThrowIfProducerFailed();
        Volatile.Write(ref _requestedItemCount, itemCount);
        _producerBarrier.SignalAndWait();
        _producerBarrier.SignalAndWait();
        ThrowIfProducerFailed();
    }

    private int ConsumeExactly(int itemCount)
    {
        var enumerator = _enumerator ?? throw new InvalidOperationException("Enumerator was not prepared.");
        var consumed = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (!moveNext.IsCompletedSuccessfully || !moveNext.Result)
                throw new InvalidOperationException("A pre-buffered recycle item was not available synchronously.");
            consumed++;
        }

        return consumed;
    }

    private void ProducerLoop()
    {
        try
        {
            while (true)
            {
                _producerBarrier.SignalAndWait();
                var shouldStop = Volatile.Read(ref _stopProducer) != 0;
                try
                {
                    if (!shouldStop)
                    {
                        var dispatcher = Volatile.Read(ref _dispatcher)
                            ?? throw new InvalidOperationException("Producer was released without a dispatcher.");
                        var itemCount = Volatile.Read(ref _requestedItemCount);
                        for (var index = 0; index < itemCount; index++)
                            dispatcher.DispatchAsync(SPayload, encodedByteCount: 1).GetAwaiter().GetResult();
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(
                        ref _producerFailure,
                        ExceptionDispatchInfo.Capture(exception),
                        null);
                    Volatile.Read(ref _dispatcher)?.Complete(exception);
                }

                _producerBarrier.SignalAndWait();
                if (shouldStop)
                    return;
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _producerFailure, ExceptionDispatchInfo.Capture(exception), null);
        }
    }

    private void ThrowIfProducerFailed()
        => Volatile.Read(ref _producerFailure)?.Throw();
}
