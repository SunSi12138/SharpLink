using System;
using System.Buffers;
using System.Runtime.CompilerServices;
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
///   <item><b>Layer 2</b>: <c>InvokeTerminalTrackedAsync</c> — awaits the terminal stub invoker and
///     sets the terminal status/elapsed.</item>
///   <item><b>Terminal</b>: <see cref="IValueTaskSource"/> (non-generic <see cref="ValueTask"/>,
///     exactly the shape produced by <c>_stub.InvokeCancellableAsync → generated bridge</c>).</item>
/// </list>
/// The chain bodies are verbatim reproductions of <c>SharpLinkServer.Interceptors.cs</c>
/// (<c>InvokeAsync</c> 613-631, <c>InvokeNextAsync</c> terminal dispatch 633-657,
/// <c>InvokeTerminalTrackedAsync</c> 844-872, <c>RecordInvocationFailure</c> 879-893,
/// <c>IsCancellationException</c> 875-877) inside a faithful <c>ServerPipelineFacts</c> value struct
/// (fields 574-585, ctor 587-611) using the real public <see cref="IRpcStub"/>,
/// <see cref="IRpcGeneratedServerBridge"/>, <see cref="IRpcByteBufferWriter"/>,
/// <see cref="SharpLinkInvocationStatus"/>, <see cref="SharpLinkErrorCode"/>, and
/// <see cref="SharpLinkException"/> types. The single internal-only field type
/// (<c>RpcSession</c>) is modelled as <c>object</c> (a reference-type field of identical size), and
/// the internal-constructor <see cref="SharpLinkServerInvocationContext"/> is reproduced as a minimal
/// context class carrying only the fields the chain mutates. No production source is modified.
/// <para>
/// Two terminal shapes are compared (plus a bare-source control): a synchronously-completed pooled
/// source (the hot path where the response lands before the await) and a genuinely suspended source
/// (completes on a thread-pool thread). On the sync path no <c>async ValueTask</c> state machine is
/// boxed (the server chain has no result box, unlike the client); on the suspended path the two
/// state machines are boxed, so the delta isolates exactly the cost issue #200 tracks.
/// </para>
/// <para>
/// Scope: this measures the <b>traditional async lowering</b> that ships in the .NET 10 SDK. The
/// runtime-async comparison axis is populated by the standalone net11 harness (issue #200's
/// baremetal net11-bench artifact, built with <c>-p:RuntimeAsync=on</c> against the same chain
/// reproduction); re-run this same matrix once runtime-async ships in a release SDK.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class ServerInterceptorChainLoweringBenchmarks
{
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

    private ServerPipelineFacts CreateFacts(IRpcStub stub) => new(
        Array.Empty<ISharpLinkServerInterceptor>(),
        stub,
        service: null!,
        session: null!,
        generatedBridge: null!,
        methodId: 0,
        requestId: 0,
        arguments: default,
        output: _output,
        timeProvider: TimeProvider.System,
        cancellationToken: CancellationToken.None);

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
    // Verbatim reproduction of the two-layer SERVER chain from SharpLinkServer.Interceptors.cs.
    // ---------------------------------------------------------------------------------

    private struct ServerPipelineFacts
    {
        // SharpLinkServer.Interceptors.cs:574-585 (RpcSession is internal; modelled as object —
        // a reference-type field of identical size/layout).
        private readonly ISharpLinkServerInterceptor[] _interceptors;
        private readonly IRpcStub _stub;
        private readonly object _service;
        private readonly object _session;
        private readonly IRpcGeneratedServerBridge _generatedBridge;
        private readonly long _methodId;
        private readonly long _requestId;
        private readonly ReadOnlySequence<byte> _arguments;
        private readonly IRpcByteBufferWriter? _output;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _cancellationToken;
        private long _started;

        // SharpLinkServer.Interceptors.cs:587-611.
        public ServerPipelineFacts(
            ISharpLinkServerInterceptor[] interceptors,
            IRpcStub stub,
            object service,
            object session,
            IRpcGeneratedServerBridge generatedBridge,
            long methodId,
            long requestId,
            ReadOnlySequence<byte> arguments,
            IRpcByteBufferWriter? output,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            _interceptors = interceptors;
            _stub = stub;
            _service = service;
            _session = session;
            _generatedBridge = generatedBridge;
            _methodId = methodId;
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

        // SharpLinkServer.Interceptors.cs:633-657 — zero interceptors → terminal directly
        // (the continuation classes are only reached when interceptors ARE registered and suspend).
        private ValueTask InvokeNextAsync(int index, ServerInvocationContext context)
            => InvokeTerminalTrackedAsync(context);

        // SharpLinkServer.Interceptors.cs:844-872 (verbatim body; response-bearing path, output non-null).
        private async ValueTask InvokeTerminalTrackedAsync(ServerInvocationContext context)
        {
            try
            {
                if (_output is null)
                {
                    await _stub.InvokeNoReturnCancellableAsync(
                        _service, _generatedBridge, _methodId, _requestId, _arguments, _cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _stub.InvokeCancellableAsync(
                        _service, _generatedBridge, _methodId, _requestId, _arguments, _output, _cancellationToken)
                        .ConfigureAwait(false);
                }
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

        // SharpLinkServer.Interceptors.cs:879-893.
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

        // SharpLinkServer.Interceptors.cs:875-877.
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

    // ---- fake terminal stubs (IRpcStub) -------------------------------------------

    private sealed class CompletedStub : IRpcStub
    {
        public CompletedSource Source { get; } = new();
        public long InterfaceHash => 0;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args)
            => Source.AsValueTask();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => Source.AsValueTask();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => Source.AsValueTask();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => Source.AsValueTask();
    }

    private sealed class SuspendedStub : IRpcStub
    {
        public SuspendedSource Source { get; } = new();
        public long InterfaceHash => 0;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args)
            => Source.AsValueTask();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => Source.AsValueTask();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => Source.AsValueTask();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
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
    /// own (box + sync overhead) is measured.
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
