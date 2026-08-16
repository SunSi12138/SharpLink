using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>
/// Isolates the client-generated-proxy <c>ValueTask&lt;T&gt; -&gt; Task&lt;T&gt;</c> bridge tracked by
/// issue #159, independent of any full RPC round-trip.
/// <list type="bullet">
///   <item><b>Variant A (current)</b>: <c>return _channel.InvokeUnaryAsync(...).AsTask();</c></item>
///   <item><b>Variant B (candidate)</b>: <c>async Task&lt;T&gt;</c> with direct-await and
///     <c>ConfigureAwait(false)</c>.</item>
///   <item><b>Variant C (control)</b>: <c>ValueTask&lt;T&gt;</c> passthrough, no Task-ification.</item>
/// </list>
/// <para>
/// The "channel" is modelled as a non-inlinable method that returns the same
/// <see cref="ValueTask{TResult}"/> shape the generated proxy receives from
/// <see cref="IRpcChannel.InvokeUnaryAsync{TRequest,TResponse}"/>. Four source shapes are compared:
/// a synchronously-completed <c>ValueTask&lt;int&gt;</c>, a completed pooled
/// <see cref="IValueTaskSource{TResult}"/>, a genuinely suspended
/// <see cref="IValueTaskSource{TResult}"/> (completes on another thread), and the no-result
/// <c>ValueTask&lt;byte&gt;.AsVoid()</c> acknowledgement path.
/// </para>
/// <para>
/// Scope: this measures the <b>traditional async lowering</b> that ships in the .NET 10 SDK.
/// The runtime-async comparison axis of issue #159 cannot be populated until a runtime-async
/// feature ships, so it is intentionally absent here (re-run this same matrix once one does).
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class ClientProxyBridgeBenchmarks
{
    private readonly CompletedSource _completedSource = new();
    private readonly SuspendedSource _suspendedSource = new();
    private readonly SuspendedByteSource _suspendedByteSource = new();

    // ---- Workload 1: ValueTask<int> synchronously completed ------------------------------

    [Benchmark]
    public Task<int> SyncValueTask_AsTask() => CallSyncValueTask().AsTask();

    [Benchmark]
    public async Task<int> SyncValueTask_DirectAwait()
    {
        return await CallSyncValueTask().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> SyncValueTask_Passthrough() => CallSyncValueTask();

    // ---- Workload 2: completed IValueTaskSource<int> (pooled operation) -------------------

    [Benchmark]
    public Task<int> CompletedSource_AsTask() => CallCompletedSource().AsTask();

    [Benchmark]
    public async Task<int> CompletedSource_DirectAwait()
    {
        return await CallCompletedSource().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> CompletedSource_Passthrough() => CallCompletedSource();

    // ---- Workload 3: genuinely suspended IValueTaskSource<int> ----------------------------

    [Benchmark]
    public Task<int> SuspendedSource_AsTask() => CallSuspendedSource().AsTask();

    [Benchmark]
    public async Task<int> SuspendedSource_DirectAwait()
    {
        return await CallSuspendedSource().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> SuspendedSource_Passthrough() => CallSuspendedSource();

    // ---- Workload 6: no-result Task (ValueTask<byte>.AsVoid() acknowledgement) ------------

    [Benchmark]
    public Task NoResult_AsVoidAsTask() => CallSyncByte().AsVoid().AsTask();

    [Benchmark]
    public async Task NoResult_DirectAwait()
    {
        await CallSyncByte().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask NoResult_ValueTask() => CallSyncByte().AsVoid();

    // ---- Workload 6b: suspended no-result acknowledgement (pooled IValueTaskSource<byte>) ---
    // Real response-less RPCs return a pooled RpcRequestOperation<byte> that stays incomplete
    // until the response arrives; only here does Variant A run the AsVoid state machine before
    // AsTask, while Variant B awaits the byte source directly.

    [Benchmark]
    public Task NoResultSuspended_AsVoidAsTask() => CallSuspendedByte().AsVoid().AsTask();

    [Benchmark]
    public async Task NoResultSuspended_DirectAwait()
    {
        await CallSuspendedByte().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask NoResultSuspended_ValueTask() => CallSuspendedByte().AsVoid();

    // ---- channel call stand-ins (non-inlinable to model interface dispatch) ---------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<int> CallSyncValueTask() => new(42);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> CallCompletedSource() => _completedSource.AsValueTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> CallSuspendedSource() => _suspendedSource.AsValueTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<byte> CallSyncByte() => new((byte)0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<byte> CallSuspendedByte() => _suspendedByteSource.AsValueTask();

    /// <summary>
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that is already completed when the
    /// proxy bridge runs. This mirrors the hot loopback case where the response lands before the
    /// caller resumes, so only the bridge's own materialization cost is measured.
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
            // Return-to-pool then reuse: re-arm and re-complete so each invocation observes a
            // freshly completed pooled operation, mirroring RpcRequestOperation<T>.GetResult.
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
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that is incomplete when the proxy bridge
    /// returns and later completes on a thread-pool thread. This is the most important micro case
    /// in issue #159: it reproduces "proxy returns -&gt; operation completes on another thread -&gt;
    /// caller await resumes -&gt; GetResult -&gt; return-to-pool" without a real transport.
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
            // Return-to-pool equivalent: re-arm for the next benchmark iteration.
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

    /// <summary>
    /// A pooled <see cref="IValueTaskSource{TResult}"/> over the internal byte acknowledgement that
    /// is incomplete at the bridge boundary and completes on a thread-pool thread. Backs the
    /// suspended no-result workload, where only Variant A runs the <c>AsVoid</c> state machine
    /// before <c>AsTask</c>, while Variant B awaits the byte source directly.
    /// </summary>
    private sealed class SuspendedByteSource : IValueTaskSource<byte>
    {
        private ManualResetValueTaskSourceCore<byte> _core;

        public SuspendedByteSource() => _core.RunContinuationsAsynchronously = true;

        public short Version => _core.Version;

        public ValueTask<byte> AsValueTask() => new(this, _core.Version);

        public byte GetResult(short token)
        {
            var result = _core.GetResult(token);
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
            ThreadPool.QueueUserWorkItem(static source => ((SuspendedByteSource)source!)._core.SetResult(0), this);
        }
    }
}
