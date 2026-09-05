using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Correctness / observable-semantics characterization for issue #159's client-proxy
/// <c>ValueTask&lt;T&gt; -&gt; Task&lt;T&gt;</c> bridge. Three bridge shapes are characterized against
/// the production generator's current <c>.AsTask()</c> behavior:
/// <list type="bullet">
///   <item><b>Variant A</b>: <c>return invoke().AsTask();</c></item>
///   <item><b>Variant B</b>: <c>async Task&lt;T&gt; { return await invoke().ConfigureAwait(false); }</c></item>
///   <item><b>Variant C</b>: <c>ValueTask&lt;T&gt;</c> passthrough.</item>
/// </list>
/// These tests do not modify the generator; they pin the observable contract so the benchmark
/// conclusion cannot silently change exception timing, continuation semantics, or the pooled
/// <see cref="IValueTaskSource{TResult}"/> lifecycle.
/// </summary>
public class ClientProxyBridgeSemanticsTests
{
    // ---- bridge shapes under test -------------------------------------------------------

    private static Task<int> AsTask(Func<ValueTask<int>> invoke) => invoke().AsTask();

    private static async Task<int> DirectAwait(Func<ValueTask<int>> invoke)
    {
        return await invoke().ConfigureAwait(false);
    }

    private static ValueTask<int> Passthrough(Func<ValueTask<int>> invoke) => invoke();

    // ---- 1. synchronous exception boundary ---------------------------------------------

    [Test]
    public async Task VariantA_ThrowsSynchronously_BeforeValueTaskIsProduced()
    {
        var threwSynchronously = false;
        try
        {
            _ = AsTask(ThrowBeforeValueTask);
        }
        catch (InvalidOperationException)
        {
            threwSynchronously = true;
        }

        await Assert.That(threwSynchronously).IsTrue();
    }

    [Test]
    public async Task VariantB_CapturesPreInvokeException_IntoReturnedTask()
    {
        var returnedTask = false;
        Task<int> pending;
        try
        {
            pending = DirectAwait(ThrowBeforeValueTask);
            returnedTask = true;
        }
        catch (InvalidOperationException)
        {
            // Variant B must not throw synchronously for a pre-await exception.
            throw new InvalidOperationException("async Task bridge threw synchronously.");
        }

        await Assert.That(returnedTask).IsTrue();
        await Assert.That(pending.IsFaulted).IsTrue();
        var caught = false;
        try
        {
            _ = await pending;
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }

        await Assert.That(caught).IsTrue();
    }

    [Test]
    public async Task VariantC_ThrowsSynchronously_BeforeValueTaskIsProduced()
    {
        var threwSynchronously = false;
        try
        {
            _ = Passthrough(ThrowBeforeValueTask);
        }
        catch (InvalidOperationException)
        {
            threwSynchronously = true;
        }

        await Assert.That(threwSynchronously).IsTrue();
    }

    // ---- 2. faulted / cancelled ValueTask propagation -----------------------------------

    [Test]
    public async Task FaultedValueTask_PropagatesIdentically_AcrossAllVariants()
    {
        foreach (var bridge in Bridges(FaultedInvoke))
        {
            var caught = false;
            try
            {
                _ = await bridge();
            }
            catch (SharpLinkException ex) when (ReferenceEquals(ex, Fault))
            {
                caught = true;
            }

            await Assert.That(caught).IsTrue();
        }
    }

    [Test]
    public async Task CanceledValueTask_PropagatesIdentically_AcrossAllVariants()
    {
        foreach (var bridge in Bridges(CanceledInvoke))
        {
            var caught = false;
            try
            {
                _ = await bridge();
            }
            catch (OperationCanceledException)
            {
                caught = true;
            }

            await Assert.That(caught).IsTrue();
        }
    }

    // ---- 3. SynchronizationContext / continuation semantics -----------------------------

    [Test]
    public async Task VariantB_ConfigureAwaitFalse_DoesNotCaptureSynchronizationContext()
    {
        var source = new CountingSource();
        var context = new RecordingSynchronizationContext();
        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var pending = DirectAwait(source.AsValueTask);
            await pending.ConfigureAwait(false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        await Assert.That(context.PostCount).IsEqualTo(0);
    }

    // ---- 4. pooled IValueTaskSource<T> lifecycle ----------------------------------------

    [Test]
    public async Task PooledSource_GetResultExactlyOnce_AcrossAllVariants()
    {
        // Variant A: .AsTask() must forward exactly one GetResult / return-to-pool.
        var variantA = new CountingSource();
        await Assert.That(await AsTask(variantA.AsValueTask)).IsEqualTo(42);
        await Assert.That(variantA.GetResultCalls).IsEqualTo(1);
        await Assert.That(variantA.ReturnToPoolCalls).IsEqualTo(1);

        // Variant B: direct-await must forward exactly one GetResult / return-to-pool.
        var variantB = new CountingSource();
        await Assert.That(await DirectAwait(variantB.AsValueTask)).IsEqualTo(42);
        await Assert.That(variantB.GetResultCalls).IsEqualTo(1);
        await Assert.That(variantB.ReturnToPoolCalls).IsEqualTo(1);

        // Variant C: passthrough must forward exactly one GetResult / return-to-pool.
        var variantC = new CountingSource();
        await Assert.That(await Passthrough(variantC.AsValueTask)).IsEqualTo(42);
        await Assert.That(variantC.GetResultCalls).IsEqualTo(1);
        await Assert.That(variantC.ReturnToPoolCalls).IsEqualTo(1);
    }

    [Test]
    public async Task PooledSource_StaleTokenIsRejected_AfterReturnToPool()
    {
        var source = new CountingSource();
        var consumedVersion = source.Version;
        await Assert.That(await DirectAwait(source.AsValueTask)).IsEqualTo(42);

        // GetResult returned the source to the pool and re-armed it with a new version. A
        // ValueTask carrying the consumed token must be rejected, not silently re-consumed.
        var stale = new ValueTask<int>(source, consumedVersion);
        var rejected = false;
        try
        {
            _ = await stale.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
    }

    // ---- fixtures -----------------------------------------------------------------------

    private static IEnumerable<Func<Task<int>>> Bridges(Func<ValueTask<int>> invoke)
    {
        yield return () => AsTask(invoke);
        yield return () => DirectAwait(invoke);
        yield return () => Passthrough(invoke).AsTask();
    }

    private static ValueTask<int> ThrowBeforeValueTask()
        => throw new InvalidOperationException("pre-value-task");

    private static readonly SharpLinkException Fault = new(SharpLinkErrorCode.DataLoss, "fault");

    private static ValueTask<int> FaultedInvoke() => ValueTask.FromException<int>(Fault);

    private static ValueTask<int> CanceledInvoke()
        => ValueTask.FromCanceled<int>(new CancellationToken(canceled: true));

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            // Still dispatch the callback so a captured continuation completes the task and the
            // assertion fails deterministically (PostCount > 0) instead of hanging the run.
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that is incomplete at the bridge boundary
    /// and completes on a thread-pool thread, counting <c>GetResult</c> and return-to-pool.
    /// </summary>
    private sealed class CountingSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core;
        private int _getResultCalls;
        private int _returnToPoolCalls;

        public CountingSource() => _core.RunContinuationsAsynchronously = true;

        public short Version => _core.Version;

        public int GetResultCalls => _getResultCalls;

        public int ReturnToPoolCalls => _returnToPoolCalls;

        public ValueTask<int> AsValueTask() => new(this, _core.Version);

        public int GetResult(short token)
        {
            Interlocked.Increment(ref _getResultCalls);
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                Interlocked.Increment(ref _returnToPoolCalls);
                _core.Reset();
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
            ThreadPool.QueueUserWorkItem(static source => ((CountingSource)source!)._core.SetResult(42), this);
        }
    }
}
