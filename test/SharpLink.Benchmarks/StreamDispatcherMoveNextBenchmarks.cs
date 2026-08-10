using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures the dispatcher read paths before changing their async state machine.
/// The suspended cases use one fixed producer thread and reusable synchronization so
/// per-item <see cref="Task"/> or <see cref="TaskCompletionSource"/> allocation cannot
/// be mistaken for dispatcher allocation.
/// </summary>
/// <remarks>
/// <para>
/// The control cases retain the same producer/consumer hand-off but bypass the dispatcher.
/// Compare them with the matching suspended case before attributing allocation to
/// <see cref="PooledAsyncStreamDispatcher{T}.MoveNextAsync"/>.
/// </para>
/// <para>
/// BenchmarkDotNet allocation numbers are a screening signal only. A production rewrite
/// still requires an allocation stack that identifies the MoveNext async state machine.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class StreamDispatcherMoveNextBenchmarks
{
    private const int LongStreamItemCount = 100_000;
    private const int BurstStreamItemCount = 1_024;

    private static readonly ReadOnlySequence<byte> SPayload = new(new byte[] { 1 });
    private static readonly ByteCodec SCodec = new();

    private readonly AutoResetEvent _producerRequest = new(initialState: false);
    private readonly AutoResetEvent _producerStopped = new(initialState: false);
    private readonly ReusableAsyncSignal _controlSignal = new();

    private Thread? _producerThread;
    private PooledAsyncStreamDispatcher<byte>? _dispatcher;
    private IAsyncEnumerator<byte>? _enumerator;
    private ExceptionDispatchInfo? _producerFailure;
    private int _producerMode;
    private int _requestedItemCount;

    [GlobalSetup]
    public void Setup()
    {
        WarmDispatcherPool();
        _producerThread = new Thread(ProducerLoop)
        {
            IsBackground = true,
            Name = "SharpLink.MoveNextBenchmarkProducer"
        };
        _producerThread.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeCurrentDispatcher();
        Volatile.Write(ref _producerMode, (int)ProducerMode.Stop);
        _producerRequest.Set();

        if (!_producerStopped.WaitOne(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The dispatcher benchmark producer did not stop.");
        _producerThread?.Join();
    }

    [IterationSetup(Target = nameof(PreBuffered_1))]
    public void SetupPreBuffered1() => PreparePreBuffered(1);

    [IterationCleanup(Target = nameof(PreBuffered_1))]
    public void CleanupPreBuffered1() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1)]
    public int PreBuffered_1() => ConsumePreBuffered(1);

    [IterationSetup(Target = nameof(PreBuffered_16))]
    public void SetupPreBuffered16() => PreparePreBuffered(16);

    [IterationCleanup(Target = nameof(PreBuffered_16))]
    public void CleanupPreBuffered16() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 16)]
    public int PreBuffered_16() => ConsumePreBuffered(16);

    [IterationSetup(Target = nameof(PreBuffered_1024))]
    public void SetupPreBuffered1024() => PreparePreBuffered(1_024);

    [IterationCleanup(Target = nameof(PreBuffered_1024))]
    public void CleanupPreBuffered1024() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1_024)]
    public int PreBuffered_1024() => ConsumePreBuffered(1_024);

    [IterationSetup(Target = nameof(AlwaysSuspend_1))]
    public void SetupAlwaysSuspend1() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(AlwaysSuspend_1))]
    public void CleanupAlwaysSuspend1() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1)]
    public ValueTask<int> AlwaysSuspend_1() => ConsumeSuspendedAsync(1, burstSize: 1);

    [IterationSetup(Target = nameof(AlwaysSuspendControl_1))]
    public void SetupAlwaysSuspendControl1() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = 1)]
    public ValueTask<int> AlwaysSuspendControl_1() => ConsumeCoordinationControlAsync(1, burstSize: 1);

    [IterationSetup(Target = nameof(AlwaysSuspend_16))]
    public void SetupAlwaysSuspend16() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(AlwaysSuspend_16))]
    public void CleanupAlwaysSuspend16() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 16)]
    public ValueTask<int> AlwaysSuspend_16() => ConsumeSuspendedAsync(16, burstSize: 1);

    [IterationSetup(Target = nameof(AlwaysSuspendControl_16))]
    public void SetupAlwaysSuspendControl16() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = 16)]
    public ValueTask<int> AlwaysSuspendControl_16() => ConsumeCoordinationControlAsync(16, burstSize: 1);

    [IterationSetup(Target = nameof(AlwaysSuspend_1024))]
    public void SetupAlwaysSuspend1024() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(AlwaysSuspend_1024))]
    public void CleanupAlwaysSuspend1024() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1_024)]
    public ValueTask<int> AlwaysSuspend_1024() => ConsumeSuspendedAsync(1_024, burstSize: 1);

    [IterationSetup(Target = nameof(AlwaysSuspendControl_1024))]
    public void SetupAlwaysSuspendControl1024() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = 1_024)]
    public ValueTask<int> AlwaysSuspendControl_1024() => ConsumeCoordinationControlAsync(1_024, burstSize: 1);

    [IterationSetup(Target = nameof(BurstProducer_8))]
    public void SetupBurstProducer8() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(BurstProducer_8))]
    public void CleanupBurstProducer8() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = BurstStreamItemCount)]
    public ValueTask<int> BurstProducer_8() => ConsumeSuspendedAsync(BurstStreamItemCount, burstSize: 8);

    [IterationSetup(Target = nameof(BurstProducerControl_8))]
    public void SetupBurstProducerControl8() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = BurstStreamItemCount)]
    public ValueTask<int> BurstProducerControl_8()
        => ConsumeCoordinationControlAsync(BurstStreamItemCount, burstSize: 8);

    [IterationSetup(Target = nameof(BurstProducer_32))]
    public void SetupBurstProducer32() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(BurstProducer_32))]
    public void CleanupBurstProducer32() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = BurstStreamItemCount)]
    public ValueTask<int> BurstProducer_32() => ConsumeSuspendedAsync(BurstStreamItemCount, burstSize: 32);

    [IterationSetup(Target = nameof(BurstProducerControl_32))]
    public void SetupBurstProducerControl32() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = BurstStreamItemCount)]
    public ValueTask<int> BurstProducerControl_32()
        => ConsumeCoordinationControlAsync(BurstStreamItemCount, burstSize: 32);

    [IterationSetup(Target = nameof(LongStream))]
    public void SetupLongStream() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(LongStream))]
    public void CleanupLongStream() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = LongStreamItemCount)]
    public ValueTask<int> LongStream() => ConsumeSuspendedAsync(LongStreamItemCount, burstSize: 32);

    [IterationSetup(Target = nameof(LongStreamControl))]
    public void SetupLongStreamControl() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = LongStreamItemCount)]
    public ValueTask<int> LongStreamControl()
        => ConsumeCoordinationControlAsync(LongStreamItemCount, burstSize: 32);

    private static void WarmDispatcherPool()
    {
        var dispatcher = PooledAsyncStreamDispatcher<byte>.Rent(default, SCodec);
        var enumerator = dispatcher.GetAsyncEnumerator();
        dispatcher.Complete(exception: null);
        enumerator.DisposeAsync().GetAwaiter().GetResult();
    }

    private void PreparePreBuffered(int itemCount)
    {
        PrepareDispatcher();
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("Benchmark dispatcher was not created.");
        for (var index = 0; index < itemCount; index++)
            dispatcher.DispatchAsync(SPayload, encodedByteCount: 1).GetAwaiter().GetResult();
        dispatcher.Complete(exception: null);
    }

    private void PrepareSuspendedDispatcher() => PrepareDispatcher();

    private void PrepareDispatcher()
    {
        ThrowIfProducerFailed();
        if (_dispatcher is not null || _enumerator is not null)
            throw new InvalidOperationException("The previous benchmark dispatcher was not cleaned up.");

        var dispatcher = PooledAsyncStreamDispatcher<byte>.Rent(default, SCodec);
        _dispatcher = dispatcher;
        _enumerator = dispatcher.GetAsyncEnumerator();
    }

    private void DisposeCurrentDispatcher()
    {
        var dispatcher = _dispatcher;
        var enumerator = _enumerator;
        _dispatcher = null;
        _enumerator = null;
        if (dispatcher is null)
            return;

        dispatcher.Complete(exception: null);
        (enumerator ?? throw new InvalidOperationException("Benchmark dispatcher has no enumerator."))
            .DisposeAsync()
            .GetAwaiter()
            .GetResult();
    }

    private int ConsumePreBuffered(int itemCount)
    {
        var enumerator = _enumerator ?? throw new InvalidOperationException("Benchmark enumerator was not created.");
        var sum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (!moveNext.IsCompletedSuccessfully || !moveNext.Result)
                throw new InvalidOperationException("The pre-buffered MoveNext operation must complete synchronously.");
            sum += enumerator.Current;
        }

        return sum;
    }

    private async ValueTask<int> ConsumeSuspendedAsync(int itemCount, int burstSize)
    {
        var enumerator = _enumerator ?? throw new InvalidOperationException("Benchmark enumerator was not created.");
        var sum = 0;
        var remainingInBurst = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (remainingInBurst == 0)
            {
                if (moveNext.IsCompleted)
                    throw new InvalidOperationException("The first MoveNext operation in each burst must suspend.");

                remainingInBurst = Math.Min(burstSize, itemCount - index);
                RequestDispatcherItems(remainingInBurst);
            }

            if (!await moveNext.ConfigureAwait(false))
                throw new InvalidOperationException("The benchmark producer ended the stream before publishing its item.");
            sum += enumerator.Current;
            remainingInBurst--;
        }

        ThrowIfProducerFailed();
        return sum;
    }

    private async ValueTask<int> ConsumeCoordinationControlAsync(int itemCount, int burstSize)
    {
        var sum = 0;
        for (var index = 0; index < itemCount;)
        {
            var burstCount = Math.Min(burstSize, itemCount - index);
            var signal = _controlSignal.WaitAsync();
            if (signal.IsCompleted)
                throw new InvalidOperationException("The control hand-off unexpectedly completed before the producer request.");

            RequestControlSignal();
            if (!await signal.ConfigureAwait(false))
                throw new InvalidOperationException("The control producer returned an invalid hand-off signal.");

            for (var burstIndex = 0; burstIndex < burstCount; burstIndex++)
                sum++;
            index += burstCount;
        }

        ThrowIfProducerFailed();
        return sum;
    }

    private void RequestDispatcherItems(int itemCount)
    {
        ThrowIfProducerFailed();
        Volatile.Write(ref _requestedItemCount, itemCount);
        Volatile.Write(ref _producerMode, (int)ProducerMode.Dispatcher);
        _producerRequest.Set();
    }

    private void RequestControlSignal()
    {
        ThrowIfProducerFailed();
        Volatile.Write(ref _producerMode, (int)ProducerMode.Control);
        _producerRequest.Set();
    }

    private void ProducerLoop()
    {
        try
        {
            while (true)
            {
                _producerRequest.WaitOne();
                switch ((ProducerMode)Volatile.Read(ref _producerMode))
                {
                    case ProducerMode.Stop:
                        return;
                    case ProducerMode.Dispatcher:
                    {
                        var dispatcher = Volatile.Read(ref _dispatcher)
                            ?? throw new InvalidOperationException("Producer was asked to dispatch without a dispatcher.");
                        var itemCount = Volatile.Read(ref _requestedItemCount);
                        for (var index = 0; index < itemCount; index++)
                            dispatcher.DispatchAsync(SPayload, encodedByteCount: 1).GetAwaiter().GetResult();
                        break;
                    }
                    case ProducerMode.Control:
                        _controlSignal.Signal();
                        break;
                    default:
                        throw new InvalidOperationException("Benchmark producer received an unknown request.");
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _producerFailure, ExceptionDispatchInfo.Capture(exception));
            Volatile.Read(ref _dispatcher)?.Complete(exception);
            _controlSignal.Signal();
        }
        finally
        {
            _producerStopped.Set();
        }
    }

    private void ThrowIfProducerFailed()
        => Volatile.Read(ref _producerFailure)?.Throw();

    private enum ProducerMode
    {
        None,
        Dispatcher,
        Control,
        Stop
    }

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

    private sealed class ReusableAsyncSignal : IValueTaskSource<bool>
    {
        private readonly Lock _gate = new();
        private ManualResetValueTaskSourceCore<bool> _source = new()
        {
            RunContinuationsAsynchronously = true
        };
        private bool _signaled;
        private bool _waiting;

        public ValueTask<bool> WaitAsync()
        {
            lock (_gate)
            {
                if (_signaled)
                {
                    _signaled = false;
                    return ValueTask.FromResult(true);
                }

                if (_waiting)
                    throw new InvalidOperationException("Only one control waiter is supported.");

                _waiting = true;
                _source.Reset();
                return new ValueTask<bool>(this, _source.Version);
            }
        }

        public void Signal()
        {
            lock (_gate)
            {
                if (!_waiting)
                {
                    _signaled = true;
                    return;
                }

                _waiting = false;
                _source.SetResult(true);
            }
        }

        public bool GetResult(short token) => _source.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}
