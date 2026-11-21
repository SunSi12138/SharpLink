using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>
/// Isolates the two remaining <c>async ValueTask</c> layers of the SERVER interceptor chain tracked
/// by issue #200, independent of any full RPC round-trip.
/// <list type="bullet">
///   <item><b>Layer 1</b>: <c>ServerPipelineFacts.InvokeAsync</c> — awaits <c>InvokeNextAsync</c> and
///     sets the completion status on success.</item>
///   <item><b>Continuation dispatch</b>: <c>InvokeNextAsync</c> → <c>ServerInterceptorContinuation</c> /
///     <c>ServerContinuationState</c> (the sharded pool + single-invocation lifecycle).</item>
///   <item><b>Layer 2</b>: <c>InvokeTerminalTrackedAsync</c> — awaits the terminal stub invoker and
///     sets the terminal status/elapsed.</item>
///   <item><b>Terminal</b>: <see cref="IValueTaskSource"/> (non-generic <see cref="ValueTask"/>,
///     driven through a prepared generated method executor).</item>
/// </list>
/// This is the historical continuation-chain model, not a current full-runtime comparison.
/// A single pass-through interceptor drives the chain. Both cases resolve the same prepared
/// <see cref="IRpcStub"/> method before measurement and then use the real
/// <see cref="RpcGeneratedMethodExecutor"/> to await the terminal.
/// <para>
/// Two terminal shapes are compared (plus a bare-source control): a synchronously-completed pooled source
/// (the hot path where the response lands before the await) and a genuinely suspended source (completes on a
/// thread-pool thread). On the sync path no <c>async ValueTask</c> state machine is boxed (the server chain
/// has no result box, unlike the client); on the suspended path the two state machines are boxed on top of
/// the continuation dispatch, so the delta isolates exactly the cost issue #200 tracks.
/// </para>
/// <para>
/// Scope: this measures the <b>traditional async lowering</b> that ships in the .NET 10 SDK. The
/// historical runtime-async comparison used issue #200's standalone baremetal net11-bench artifact
/// and its harness-specific <c>-p:RuntimeAsync=on</c> switch. That artifact is not a current GA run.
/// For a new comparison, use eng/net11/global.json and the compiler's explicit
/// <c>Features=runtime-async=on</c> opt-in in the separate net11 project.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class ServerInterceptorChainLoweringBenchmarks
{
    private const long MethodId = 1;
    private static readonly IServerInterceptor[] Interceptors = [new PassThroughInterceptor()];

    private ServerPipelineFacts _syncFacts;
    private ServerPipelineFacts _suspendedFacts;
    private readonly ServerInvocationContext _syncContext = new();
    private readonly ServerInvocationContext _suspendedContext = new();
    private readonly CompletedStub _completedStub = new();
    private readonly SuspendedStub _suspendedStub = new();
    private readonly NoopOutput _output = new();
    private readonly ManualResetEventSlim _controlGate = new(initialState: false);
    private Action? _controlContinuation;

    [GlobalSetup]
    public void Setup()
    {
        _controlContinuation = _controlGate.Set;
        _syncFacts = CreateFacts(_completedStub);
        _suspendedFacts = CreateFacts(_suspendedStub);
    }

    private ServerPipelineFacts CreateFacts(IRpcStub stub)
    {
        if (!stub.TryPrepareMethod(MethodId, out var method))
            throw new InvalidOperationException("The benchmark terminal could not be prepared.");
        return new(Interceptors, method, service: null!, session: null!, generatedBridge: null!,
            requestId: 0, arguments: default, output: _output, timeProvider: TimeProvider.System,
            cancellationToken: CancellationToken.None);
    }

    // ---- chain cases ---------------------------------------------------------------
    // The context is reused across operations, so its Status is reset to Pending before each
    // invocation to mirror production, which receives a fresh Pending context per RPC. Without
    // this, the first call leaves Status=Succeeded and every subsequent call would skip the
    // `if (Status == Pending) Status = Succeeded` assignment in InvokeTerminalTrackedAsync,
    // benchmarking a slightly different hot path. The reset is a single 0-allocation field write
    // and does not affect the allocation attribution (the tracked metric).

    [Benchmark]
    public ValueTask Chain_SyncCompleted()
    {
        _syncContext.Status = SharpLinkInvocationStatus.Pending;
        return _syncFacts.InvokeAsync(_syncContext);
    }

    [Benchmark]
    public ValueTask Chain_Suspended()
    {
        _suspendedContext.Status = SharpLinkInvocationStatus.Pending;
        return _suspendedFacts.InvokeAsync(_suspendedContext);
    }

    // ---- control cases (bare non-generic IValueTaskSource, no chain) ---------------

    [Benchmark]
    public void Control_SyncCompleted() => CallCompleted().GetAwaiter().GetResult();

    [Benchmark]
    public int Control_Suspended() => DriveControlSuspend();

    // ---- terminal source stand-ins (non-inlinable to model interface dispatch) ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask CallCompleted() => _completedStub.Source.AsValueTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask CallSuspended() => _suspendedStub.Source.AsValueTask();

    // Raw IValueTaskSource GetResult() cannot block an incomplete source: register a cached
    // continuation (captured once in GlobalSetup, via UnsafeOnCompleted to avoid ExecutionContext
    // capture) on a shared ManualResetEventSlim, then wait for the signal.
    private int DriveControlSuspend()
    {
        var awaiter = CallSuspended().ConfigureAwait(false).GetAwaiter();
        if (!awaiter.IsCompleted)
        {
            _controlGate.Reset();
            awaiter.UnsafeOnCompleted(_controlContinuation ?? throw new InvalidOperationException(
                "The control gate callback was not initialized."));
            _controlGate.Wait();
        }

        awaiter.GetResult();
        return 0;
    }

    // ---------------------------------------------------------------------------------
    // Historical model of the server continuation chain.
    // ---------------------------------------------------------------------------------

    private struct ServerPipelineFacts
    {
        // SharpLinkServer.Interceptors.cs:574-585 (RpcSession is internal; modelled as object — a
        // reference-type field of identical size/layout; ISharpLinkServerInterceptor is reproduced
        // as the reference-type IServerInterceptor stand-in).
        private readonly IServerInterceptor[] _interceptors;
        private readonly RpcGeneratedPreparedMethod _method;
        private readonly object _service;
        private readonly object _session;
        private readonly IRpcGeneratedServerBridge _generatedBridge;
        private readonly long _requestId;
        private readonly ReadOnlySequence<byte> _arguments;
        private readonly IRpcByteBufferWriter? _output;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _cancellationToken;
        private long _started;

        // SharpLinkServer.Interceptors.cs:587-611.
        public ServerPipelineFacts(
            IServerInterceptor[] interceptors,
            RpcGeneratedPreparedMethod method,
            object service,
            object session,
            IRpcGeneratedServerBridge generatedBridge,
            long requestId,
            ReadOnlySequence<byte> arguments,
            IRpcByteBufferWriter? output,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            _interceptors = interceptors;
            _method = method;
            _service = service;
            _session = session;
            _generatedBridge = generatedBridge;
            _requestId = requestId;
            _arguments = arguments;
            _output = output;
            _timeProvider = timeProvider;
            _cancellationToken = cancellationToken;
        }

        // SharpLinkServer.Interceptors.cs:613-631 (verbatim body; context type reproduced because
        // SharpLinkServerInvocationContext's constructor is internal).
        public async ValueTask InvokeAsync(ServerInvocationContext context)
        {
            _started = _timeProvider.GetTimestamp();
            try
            {
                await InvokeNextAsync(0, context).ConfigureAwait(false);
                if (context.Status == SharpLinkInvocationStatus.Pending)
                    context.Status = SharpLinkInvocationStatus.Succeeded;
            }
            catch (Exception exception)
            {
                RecordInvocationFailure(context, exception);
                throw;
            }
            finally
            {
                context.Elapsed = _timeProvider.GetElapsedTime(_started);
            }
        }

        // SharpLinkServer.Interceptors.cs:633-657 (verbatim body).
        private ValueTask InvokeNextAsync(int index, ServerInvocationContext context)
        {
            if (index >= _interceptors.Length)
                return InvokeTerminalTrackedAsync(context);

            var continuation = new ServerInterceptorContinuation(
                ServerContinuationState.Rent(this, index + 1));
            ValueTask invocation;
            try
            {
                invocation = _interceptors[index].InvokeAsync(context, continuation.InvokeAsync);
            }
            catch (Exception exception)
            {
                invocation = ValueTask.FromException(exception);
            }
            if (!invocation.IsCompletedSuccessfully)
            {
                if (continuation.IsSameInvocation(invocation))
                    return invocation;
                return AwaitInterceptorAsync(invocation, continuation);
            }
            EnsureResponseContinuationInvoked(continuation);
            return continuation.JoinAsync();
        }

        // SharpLinkServer.Interceptors.cs:659-690 (verbatim body).
        private async ValueTask AwaitInterceptorAsync(
            ValueTask invocation,
            ServerInterceptorContinuation continuation)
        {
            Exception? invocationException = null;
            try
            {
                await invocation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                invocationException = exception;
            }

            if (invocationException is null)
                EnsureResponseContinuationInvoked(continuation);
            try
            {
                await continuation.JoinAsync().ConfigureAwait(false);
            }
            catch (Exception continuationException) when (
                ReferenceEquals(invocationException, continuationException))
            {
                // The interceptor awaited next and propagated the same failure.
            }
            catch (Exception continuationException) when (invocationException is not null)
            {
                throw new AggregateException(invocationException, continuationException);
            }
            if (invocationException is not null)
                ExceptionDispatchInfo.Capture(invocationException).Throw();
        }

        // SharpLinkServer.Interceptors.cs:692-699 (verbatim body).
        private void EnsureResponseContinuationInvoked(ServerInterceptorContinuation continuation)
        {
            if (_output is not null && !continuation.WasInvoked)
            {
                throw new InvalidOperationException(
                    "A Server interceptor must invoke its continuation for a response-bearing RPC.");
            }
        }

        // SharpLinkServer.Interceptors.cs:701-735 (verbatim body).
        private sealed class ServerInterceptorContinuation(ServerContinuationState state)
        {
            private int _invoked;
            private ServerContinuationState? _state = state;

            public bool WasInvoked => Volatile.Read(ref _invoked) != 0;

            public ValueTask InvokeAsync(ServerInvocationContext context)
            {
                if (Interlocked.Exchange(ref _invoked, 1) != 0)
                {
                    return ValueTask.FromException(
                        new InvalidOperationException("An interceptor continuation can only be invoked once."));
                }
                return (_state ?? throw new InvalidOperationException("The interceptor continuation has expired."))
                    .InvokeAsync(context);
            }

            public ValueTask JoinAsync()
            {
                var state = Interlocked.Exchange(ref _state, null);
                return state is null ? ValueTask.CompletedTask : state.JoinAndReturnAsync();
            }

            public bool IsSameInvocation(ValueTask invocation)
            {
                var state = _state;
                if (state is null || !state.IsSameInvocation(invocation))
                    return false;
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, null, state), state))
                    return false;
                state.Return();
                return true;
            }
        }

        // SharpLinkServer.Interceptors.cs:737-842 (verbatim body).
        private sealed class ServerContinuationState
        {
            private const int MaxRetained = 4096;
            private const int ShardCount = 32;
            private static readonly Shard[] Shards = CreateShards();

            private ServerPipelineFacts _owner;
            private bool _hasOwner;
            private int _nextIndex;
            private ValueTask _completion;
            private int _completionAvailable;

            public static ServerContinuationState Rent(ServerPipelineFacts owner, int nextIndex)
            {
                var shard = Shards[Thread.CurrentThread.ManagedThreadId & (ShardCount - 1)];
                ServerContinuationState state;
                lock (shard.Gate)
                {
                    if (shard.Stack.TryPop(out state!))
                    {
                        shard.Retained--;
                    }
                    else
                    {
                        state = new ServerContinuationState();
                    }
                }
                state._owner = owner;
                state._hasOwner = true;
                state._nextIndex = nextIndex;
                return state;
            }

            public ValueTask InvokeAsync(ServerInvocationContext context)
            {
                var invocation = _hasOwner
                    ? _owner.InvokeNextAsync(_nextIndex, context)
                    : throw new InvalidOperationException("The interceptor continuation has expired.");
                _completion = invocation;
                Volatile.Write(ref _completionAvailable, 1);
                return invocation;
            }

            public bool IsSameInvocation(ValueTask invocation)
                => Volatile.Read(ref _completionAvailable) != 0 && _completion.Equals(invocation);

            public ValueTask JoinAndReturnAsync()
            {
                if (Volatile.Read(ref _completionAvailable) == 0 || _completion.IsCompleted)
                {
                    Return();
                    return ValueTask.CompletedTask;
                }
                return AwaitCompletionAndReturnAsync(this, _completion);
            }

            public void Return()
            {
                _owner = default;
                _hasOwner = false;
                _nextIndex = 0;
                _completion = default;
                Volatile.Write(ref _completionAvailable, 0);

                var returnShard = Shards[Thread.CurrentThread.ManagedThreadId & (ShardCount - 1)];
                lock (returnShard.Gate)
                {
                    if (returnShard.Retained < returnShard.Max)
                    {
                        returnShard.Retained++;
                        returnShard.Stack.Push(this);
                    }
                }
            }

            private static Shard[] CreateShards()
            {
                var shards = new Shard[ShardCount];
                var perShard = MaxRetained / ShardCount;
                for (var index = 0; index < ShardCount; index++)
                    shards[index] = new Shard(perShard);
                return shards;
            }

            private sealed class Shard(int max)
            {
                public readonly int Max = max;
                public readonly Lock Gate = new();
                public readonly Stack<ServerContinuationState> Stack = new(4);
                public int Retained;
            }

            private static async ValueTask AwaitCompletionAndReturnAsync(
                ServerContinuationState state,
                ValueTask completion)
            {
                try
                {
                    await completion.ConfigureAwait(false);
                }
                finally
                {
                    state.Return();
                }
            }
        }

        // Uses the already-prepared terminal; the chain does not look up a method on invocation.
        private async ValueTask InvokeTerminalTrackedAsync(ServerInvocationContext context)
        {
            try
            {
                var descriptor = _method.Descriptor;
                await _method.Execute(_service, _generatedBridge, in descriptor, _requestId, _arguments, _output,
                    _cancellationToken).ConfigureAwait(false);
                if (context.Status == SharpLinkInvocationStatus.Pending)
                    context.Status = SharpLinkInvocationStatus.Succeeded;
            }
            catch (Exception exception)
            {
                RecordInvocationFailure(context, exception);
                throw;
            }
            finally
            {
                context.Elapsed = _timeProvider.GetElapsedTime(_started);
            }
        }

        // SharpLinkServer.Interceptors.cs:879-893 (verbatim body).
        private static void RecordInvocationFailure(ServerInvocationContext context, Exception exception)
        {
            var cancelled = IsCancellationException(exception);
            context.Status = cancelled
                ? SharpLinkInvocationStatus.Cancelled
                : SharpLinkInvocationStatus.Failed;
            context.ErrorCode = cancelled
                ? SharpLinkErrorCode.Cancelled
                : exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
            context.Exception = exception;
        }

        // SharpLinkServer.Interceptors.cs:875-877 (verbatim body).
        private static bool IsCancellationException(Exception exception)
            => exception is OperationCanceledException or
               SharpLinkException { Code: SharpLinkErrorCode.Cancelled };
    }

    /// <summary>
    /// Mutable control data of one intercepted server call, mirroring
    /// <see cref="SharpLinkServerInvocationContext"/> (its constructor is internal, so the benchmark
    /// reproduces only the fields the chain mutates). Allocated once per chain and reused.
    /// </summary>
    private sealed class ServerInvocationContext
    {
        public SharpLinkInvocationStatus Status;
        public SharpLinkErrorCode? ErrorCode;
        public Exception? Exception;
        public TimeSpan Elapsed;
    }

    // ---- interceptor stand-ins -----------------------------------------------------
    // ISharpLinkServerInterceptor / SharpLinkServerInvocationDelegate reference the internal-ctor
    // SharpLinkServerInvocationContext, so the benchmark reproduces them with the ServerInvocationContext
    // stand-in. Both are reference types, preserving the struct field layout.

    private delegate ValueTask ServerInvocationDelegate(ServerInvocationContext context);

    private interface IServerInterceptor
    {
        ValueTask InvokeAsync(ServerInvocationContext context, ServerInvocationDelegate next);
    }

    private sealed class PassThroughInterceptor : IServerInterceptor
    {
        public ValueTask InvokeAsync(ServerInvocationContext context, ServerInvocationDelegate next)
            => next(context);
    }

    // ---- fake terminal stubs (IRpcStub) -------------------------------------------

    private sealed class CompletedStub : IRpcStub
    {
        public CompletedSource Source { get; } = new();
        public long InterfaceHash => 0;
        public bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method)
        {
            method = methodId == MethodId
                ? new(new RpcMethodDescriptor(InterfaceHash, methodId, RpcMethodKind.Unary, true, false, false, null)
                    { SupportsCancellation = true }, Execute)
                : default;
            return methodId == MethodId;
        }

        private ValueTask Execute(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte>? output, CancellationToken cancellationToken)
            => Source.AsValueTask();
    }

    private sealed class SuspendedStub : IRpcStub
    {
        public SuspendedSource Source { get; } = new();
        public long InterfaceHash => 0;
        public bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method)
        {
            method = methodId == MethodId
                ? new(new RpcMethodDescriptor(InterfaceHash, methodId, RpcMethodKind.Unary, true, false, false, null)
                    { SupportsCancellation = true }, Execute)
                : default;
            return methodId == MethodId;
        }

        private ValueTask Execute(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte>? output, CancellationToken cancellationToken)
            => Source.AsValueTask();
    }

    /// <summary>A trivial no-op response buffer writer; the fake stubs never write to it, so all
    /// members return empty/default. Exists only to satisfy the response-bearing
    /// <see cref="IRpcByteBufferWriter"/> field of the reproduced struct.</summary>
    private sealed class NoopOutput : IRpcByteBufferWriter
    {
        public int WrittenCount => 0;
        public ReadOnlyMemory<byte> WrittenMemory => ReadOnlyMemory<byte>.Empty;
        public Span<byte> WrittenSpan => Span<byte>.Empty;
        public int Capacity => 0;
        public void Clear() { }
        public void Advance(int count) { }
        public Memory<byte> GetMemory(int sizeHint = 0) => Memory<byte>.Empty;
        public Span<byte> GetSpan(int sizeHint = 0) => Span<byte>.Empty;
        public void Dispose() { }
    }

    /// <summary>
    /// A pooled <see cref="IValueTaskSource"/> that is already completed when the chain runs. Mirrors
    /// the hot loopback case where the response lands before the caller resumes, so only the chain's
    /// own (box + continuation dispatch + sync overhead) is measured.
    /// </summary>
    private sealed class CompletedSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core;

        public CompletedSource()
        {
            _core.RunContinuationsAsynchronously = true;
            _core.SetResult(true);
        }

        public short Version => _core.Version;

        public ValueTask AsValueTask() => new(this, _core.Version);

        public void GetResult(short token)
        {
            _core.GetResult(token);
            // Return-to-pool then reuse (mirrors RpcRequestOperation<T>.GetResult).
            _core.Reset();
            _core.SetResult(true);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    /// <summary>
    /// A pooled <see cref="IValueTaskSource"/> that is incomplete when the chain runs and later
    /// completes on a thread-pool thread. Reproduces "terminal invoker returns → operation completes
    /// on another thread → await resumes → GetResult → return-to-pool" without a transport, forcing
    /// both async layers to suspend and box their state machines.
    /// </summary>
    private sealed class SuspendedSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core;

        public SuspendedSource() => _core.RunContinuationsAsynchronously = true;

        public short Version => _core.Version;

        public ValueTask AsValueTask() => new(this, _core.Version);

        public void GetResult(short token)
        {
            _core.GetResult(token);
            // Return-to-pool equivalent: re-arm for the next iteration.
            _core.Reset();
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
            // Complete on another thread, mirroring the IO-thread response path.
            ThreadPool.QueueUserWorkItem(static source => ((SuspendedSource)source!)._core.SetResult(true), this);
        }
    }
}
