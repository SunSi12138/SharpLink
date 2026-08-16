using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>
/// Isolates the two remaining <c>async ValueTask</c> layers of the client interceptor chain
/// tracked by issue #199, independent of any full RPC round-trip.
/// <list type="bullet">
///   <item><b>Layer 1</b>: <c>RunTypedChainAsync&lt;TResponse&gt;</c> — awaits
///     <c>InvokeNextAsync</c> and unpacks via <c>SharpLinkClientInvocationResult.GetValue&lt;T&gt;()</c>.</item>
///   <item><b>Layer 2</b>: <c>InvokeTerminalAsync</c> — awaits the terminal unary invoker and boxes the
///     response into <see cref="SharpLinkClientInvocationResult"/>.</item>
///   <item><b>Terminal</b>: <see cref="IValueTaskSource{TResult}"/> (exactly the shape produced by
///     <c>InvokeUnaryCoreAsync → StartUnaryCall → operation.AsValueTask()</c>).</item>
/// </list>
/// The chain bodies are verbatim reproductions of <c>SharpLinkClient.Interceptors.cs</c>
/// (<c>RunTypedChainAsync</c>, <c>InvokeNextAsync</c> terminal dispatch, <c>InvokeTerminalAsync</c>,
/// <c>MarkChainSucceeded</c>/<c>MarkTerminalSucceeded</c>/<c>MarkTerminalFailed</c>/
/// <c>MarkTerminalElapsed</c>/<c>ValidateResult</c>/<c>IsCancellationException</c>) using the real
/// public <see cref="SharpLinkClientInvocationResult"/>, <see cref="SharpLinkInvocationStatus"/>,
/// <see cref="SharpLinkErrorCode"/>, and <see cref="SharpLinkException"/> types.
/// <para>
/// Two terminal shapes are compared (plus a bare-source control):
/// a synchronously-completed pooled source (the hot path where the response lands before the await)
/// and a genuinely suspended source (completes on a thread-pool thread). On the sync path no
/// <c>async ValueTask</c> state machine is boxed, so the measured allocation is the
/// <see cref="SharpLinkClientInvocationResult"/> object-box alone; on the suspended path the two
/// state machines are boxed on top of it, so the delta isolates exactly the cost issue #199 tracks.
/// </para>
/// <para>
/// Scope: this measures the <b>traditional async lowering</b> that ships in the .NET 10 SDK. The
/// runtime-async comparison axis is populated by the standalone net11 harness (issue #199's
/// baremetal net11-bench artifact, built with <c>-p:RuntimeAsync=on</c> against the same chain
/// reproduction); re-run this same matrix once runtime-async ships in a release SDK.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class InterceptorChainLoweringBenchmarks
{
    private readonly SyncCompletedChain _syncChain = new();
    private readonly SuspendedChain _suspendedChain = new();
    private readonly CompletedSource _completedSource = new();
    private readonly SuspendedSource _suspendedSource = new();
    private readonly ManualResetEventSlim _controlGate = new(initialState: false);

    // ---- chain cases ---------------------------------------------------------------

    [Benchmark]
    public ValueTask<int> Chain_SyncCompleted() => _syncChain.InvokeTypedAsync();

    [Benchmark]
    public ValueTask<int> Chain_Suspended() => _suspendedChain.InvokeTypedAsync();

    // ---- control cases (bare IValueTaskSource, no chain) ---------------------------

    [Benchmark]
    public int Control_SyncCompleted() => CallCompleted().GetAwaiter().GetResult();

    [Benchmark]
    public int Control_Suspended() => DriveControlSuspend();

    // ---- terminal source stand-ins (non-inlinable to model interface dispatch) ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> CallCompleted() => _completedSource.AsValueTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> CallSuspended() => _suspendedSource.AsValueTask();

    // Raw IValueTaskSource GetResult() cannot block an incomplete source: register the completion
    // as a continuation on a shared ManualResetEventSlim, then wait for the signal.
    private int DriveControlSuspend()
    {
        var awaiter = CallSuspended().ConfigureAwait(false).GetAwaiter();
        if (!awaiter.IsCompleted)
        {
            _controlGate.Reset();
            awaiter.OnCompleted(_controlGate.Set);
            _controlGate.Wait();
        }

        return awaiter.GetResult();
    }

    // ---------------------------------------------------------------------------------
    // Verbatim reproduction of the two-layer chain from SharpLinkClient.Interceptors.cs.
    // The terminal layer (InvokeUnaryWithOptionalRetryAsync → InvokeUnaryCoreAsync →
    // operation.AsValueTask()) is modelled as the abstract InvokeUnaryTerminalAsync(), which the
    // two subclasses back with a completed vs suspended IValueTaskSource<int>.
    // ---------------------------------------------------------------------------------

    private abstract class UnaryChain
    {
        private readonly TimeProvider _timeProvider = TimeProvider.System;
        private readonly InvocationContext _context = new();
        private long _started;

        public ValueTask<int> InvokeTypedAsync() => RunTypedChainAsync<int>();

        // SharpLinkClient.Interceptors.cs:110-129 (verbatim body, terminal inlined to
        // InvokeNextAsync → InvokeTerminalAsync for the zero-interceptor chain).
        private async ValueTask<TResult> RunTypedChainAsync<TResult>()
        {
            _started = _timeProvider.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(_context).ConfigureAwait(false);
                ValidateResult(result);
                MarkChainSucceeded(_context);
                return result.GetValue<TResult>();
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(_context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(_context);
            }
        }

        // SharpLinkClient.Interceptors.cs:157-162 — zero interceptors → terminal directly.
        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(InvocationContext context)
            => InvokeTerminalAsync(context);

        // SharpLinkClient.Interceptors.cs:438-459 (verbatim body; ResolveCallControl +
        // InvokeUnaryWithOptionalRetryAsync collapsed to the abstract terminal call).
        private async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(InvocationContext context)
        {
            try
            {
                var response = await InvokeUnaryTerminalAsync().ConfigureAwait(false);
                MarkTerminalSucceeded(context);
                return new SharpLinkClientInvocationResult(response);
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
        }

        // Stands in for InvokeUnaryWithOptionalRetryAsync → InvokeUnaryCoreAsync → operation.AsValueTask().
        protected abstract ValueTask<int> InvokeUnaryTerminalAsync();

        // SharpLinkClient.Interceptors.cs:431-436 (UnaryInterceptorState.ValidateResult, verbatim
        // body with the null check elided: for TResponse=int, default(TResponse) is null is false).
        protected void ValidateResult(SharpLinkClientInvocationResult result)
            => _ = result.GetValue<int>();

        // SharpLinkClient.Interceptors.cs:131-135.
        protected void MarkChainSucceeded(InvocationContext context)
        {
            if (context.Status == SharpLinkInvocationStatus.Pending)
                context.Status = SharpLinkInvocationStatus.Succeeded;
        }

        // SharpLinkClient.Interceptors.cs:372-373.
        protected void MarkTerminalSucceeded(InvocationContext context)
            => context.Status = SharpLinkInvocationStatus.Succeeded;

        // SharpLinkClient.Interceptors.cs:375-390.
        protected void MarkTerminalFailed(InvocationContext context, Exception exception)
        {
            if (IsCancellationException(exception))
            {
                context.Status = SharpLinkInvocationStatus.Cancelled;
                context.ErrorCode = SharpLinkErrorCode.Cancelled;
            }
            else
            {
                context.Status = SharpLinkInvocationStatus.Failed;
                context.ErrorCode = exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
            }
            context.Exception = exception;
        }

        // SharpLinkClient.Interceptors.cs:392-393.
        protected void MarkTerminalElapsed(InvocationContext context)
            => context.Elapsed = _timeProvider.GetElapsedTime(_started);

        // SharpLinkClient.Interceptors.cs:400-402.
        private static bool IsCancellationException(Exception exception)
            => exception is OperationCanceledException or
               SharpLinkException { Code: SharpLinkErrorCode.Cancelled };
    }

    private sealed class SyncCompletedChain : UnaryChain
    {
        private readonly CompletedSource _source = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override ValueTask<int> InvokeUnaryTerminalAsync() => _source.AsValueTask();
    }

    private sealed class SuspendedChain : UnaryChain
    {
        private readonly SuspendedSource _source = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override ValueTask<int> InvokeUnaryTerminalAsync() => _source.AsValueTask();
    }

    /// <summary>
    /// Mutable control data of one intercepted call, mirroring
    /// <see cref="SharpLinkClientInvocationContext"/> (its constructor is internal, so the
    /// benchmark reproduces only the fields the chain mutates). Allocated once per chain and reused;
    /// its per-call allocation is already attributed separately by
    /// <see cref="InterceptorAttributionEvidenceRunner"/>.
    /// </summary>
    private sealed class InvocationContext
    {
        public SharpLinkInvocationStatus Status;
        public SharpLinkErrorCode? ErrorCode;
        public Exception? Exception;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that is already completed when the chain
    /// runs. Mirrors the hot loopback case where the response lands before the caller resumes, so
    /// only the chain's own (box + sync overhead) is measured.
    /// </summary>
    private sealed class CompletedSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core;

        public CompletedSource()
        {
            _core.RunContinuationsAsynchronously = true;
            _core.SetResult(42);
        }

        public short Version => _core.Version;

        public ValueTask<int> AsValueTask() => new(this, _core.Version);

        public int GetResult(short token)
        {
            var result = _core.GetResult(token);
            // Return-to-pool then reuse (mirrors RpcRequestOperation<T>.GetResult).
            _core.Reset();
            _core.SetResult(42);
            return result;
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
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that is incomplete when the chain runs and
    /// later completes on a thread-pool thread. Reproduces "terminal invoker returns → operation
    /// completes on another thread → await resumes → GetResult → return-to-pool" without a transport,
    /// forcing both async layers to suspend and box their state machines.
    /// </summary>
    private sealed class SuspendedSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core;

        public SuspendedSource() => _core.RunContinuationsAsynchronously = true;

        public short Version => _core.Version;

        public ValueTask<int> AsValueTask() => new(this, _core.Version);

        public int GetResult(short token)
        {
            var result = _core.GetResult(token);
            // Return-to-pool equivalent: re-arm for the next iteration.
            _core.Reset();
            return result;
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
            ThreadPool.QueueUserWorkItem(static source => ((SuspendedSource)source!)._core.SetResult(42), this);
        }
    }
}
