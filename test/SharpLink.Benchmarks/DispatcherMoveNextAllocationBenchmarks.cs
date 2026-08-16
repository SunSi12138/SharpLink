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
/// Isolates and attributes the managed allocation of
/// <see cref="PooledAsyncStreamDispatcher{T}.MoveNextAsync"/> across its two consumer paths,
/// so a runtime-async Go/No-Go decision can be grounded in where the bytes actually come from.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher's consumer entry point is <c>async ValueTask&lt;bool&gt; MoveNextAsync()</c>.
/// When it observes a pre-buffered item it completes synchronously and must allocate nothing;
/// when it suspends it awaits a pooled <see cref="ManualResetValueTaskSourceCore{TResult}"/>
/// (<see cref="PooledAsyncStreamDispatcher{T}"/> itself implements <see cref="IValueTaskSource{T}"/>)
/// and the compiler boxes the outer async state machine onto the heap.
/// </para>
/// <para>
/// Attribution strategy: every case is a synchronous method with no benchmark-side async state
/// machine, so the reported allocation is the dispatcher/control work itself, not the harness.
/// <c>AlwaysSuspend_1</c> drives one suspending <c>MoveNextAsync()</c> and blocks on its result;
/// <c>AlwaysSuspendControl_1</c> replays the same producer/consumer hand-off through a bare
/// <see cref="ManualResetValueTaskSourceCore{TResult}"/> with no dispatcher and no outer
/// <c>MoveNextAsync</c> state machine. Their delta is the cost attributable to the outer
/// <c>MoveNextAsync</c> async state machine plus the dispatcher's wait-owner bookkeeping.
/// <c>PreBuffered_1</c> proves the synchronous fast path stays allocation-free.
/// </para>
/// <para>
/// This complements <see cref="StreamDispatcherMoveNextBenchmarks"/> (the full matrix) by narrowing
/// the screening allocation number down to the specific state machine the runtime-async lowering
/// would have to elide.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class DispatcherMoveNextAllocationBenchmarks
{
    private static readonly ReadOnlySequence<byte> SPayload = new(new byte[] { 1 });
    private static readonly ByteCodec SCodec = new();

    private readonly AutoResetEvent _producerRequest = new(initialState: false);
    private readonly AutoResetEvent _producerStopped = new(initialState: false);
    private readonly ControlSignal _controlSignal = new();
    private readonly ManualResetEventSlim _controlGate = new(initialState: false);

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
            Name = "SharpLink.MoveNextAllocationProducer"
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
            throw new TimeoutException("The dispatcher allocation benchmark producer did not stop.");
        _producerThread?.Join();
    }

    [IterationSetup(Target = nameof(PreBuffered_1))]
    public void SetupPreBuffered1() => PreparePreBuffered(1);

    [IterationCleanup(Target = nameof(PreBuffered_1))]
    public void CleanupPreBuffered1() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1)]
    public int PreBuffered_1() => ConsumePreBuffered(1);

    [IterationSetup(Target = nameof(AlwaysSuspend_1))]
    public void SetupAlwaysSuspend1() => PrepareSuspendedDispatcher();

    [IterationCleanup(Target = nameof(AlwaysSuspend_1))]
    public void CleanupAlwaysSuspend1() => DisposeCurrentDispatcher();

    [Benchmark(OperationsPerInvoke = 1)]
    public int AlwaysSuspend_1() => ConsumeSuspendedSync(1);

    [IterationSetup(Target = nameof(AlwaysSuspendControl_1))]
    public void SetupAlwaysSuspendControl1() => ThrowIfProducerFailed();

    [Benchmark(OperationsPerInvoke = 1)]
    public int AlwaysSuspendControl_1() => ConsumeControlSync(1);

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

    private int ConsumeSuspendedSync(int itemCount)
    {
        var enumerator = _enumerator ?? throw new InvalidOperationException("Benchmark enumerator was not created.");
        var sum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (moveNext.IsCompleted)
                throw new InvalidOperationException("The MoveNext operation must suspend before its producer is requested.");

            RequestDispatcherItems(1);
            if (!moveNext.GetAwaiter().GetResult())
                throw new InvalidOperationException("The benchmark producer ended the stream before publishing its item.");
            sum += enumerator.Current;
        }

        ThrowIfProducerFailed();
        return sum;
    }

    private int ConsumeControlSync(int itemCount)
    {
        var sum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var signal = RequestControlSignal();
            var awaiter = signal.ConfigureAwait(false).GetAwaiter();
            _controlGate.Reset();
            awaiter.OnCompleted(_controlGate.Set);
            _controlGate.Wait();
            if (!awaiter.GetResult())
                throw new InvalidOperationException("The control producer returned an invalid hand-off signal.");
            sum++;
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

    private ValueTask<bool> RequestControlSignal()
    {
        ThrowIfProducerFailed();
        var signal = _controlSignal.WaitAsync();
        if (signal.IsCompleted)
            throw new InvalidOperationException("The control hand-off unexpectedly completed before the producer request.");
        Volatile.Write(ref _producerMode, (int)ProducerMode.Control);
        _producerRequest.Set();
        return signal;
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

    private sealed class ControlSignal : IValueTaskSource<bool>
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
