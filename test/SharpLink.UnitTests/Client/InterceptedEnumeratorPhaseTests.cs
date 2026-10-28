using System.Reflection;
using System.Threading.Tasks.Sources;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Run this exact test file against both ac17c8d8 and the representation candidate.
// Reflection isolates the private iterator from unrelated logical/dynamic wrappers.
public class InterceptedEnumeratorPhaseTests
{
    private static IAsyncEnumerable<int> Wrap(IAsyncEnumerable<int> stream, CancellationToken invocationCancellation = default)
        => WrapInvocation(ValueTask.FromResult(new SharpLinkClientInvocationResult(stream)), invocationCancellation);

    private static IAsyncEnumerable<int> WrapInvocation(ValueTask<SharpLinkClientInvocationResult> invocation, CancellationToken invocationCancellation = default,
        RpcDeadline deadline = default, TimeProvider? timeProvider = null)
    {
        var definition = typeof(SharpLinkClient).GetNestedType("InterceptedAsyncEnumerable`1", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("intercepted iterator type missing");
        return (IAsyncEnumerable<int>)Activator.CreateInstance(definition.MakeGenericType(typeof(int)),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [invocation, false,
             deadline, timeProvider ?? TimeProvider.System, null!, invocationCancellation], null)!;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UserContextMutationsDoNotLeakAcrossMoveOrDispose(bool pending)
    {
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var stream = new ContextStream(ambient, pending);
        var iterator = Wrap(stream).GetAsyncEnumerator();
        var previousContext = SynchronizationContext.Current;
        var callerContext = new SynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            var move = iterator.MoveNextAsync();
            Ensure(ambient.Value == "caller", "GetEnumerator/MoveNext AsyncLocal changes escaped");
            Ensure(ReferenceEquals(SynchronizationContext.Current, callerContext), "synchronous producer SyncContext change escaped");
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (pending)
                stream.Release.TrySetResult(true);
            Ensure(await move.ConfigureAwait(false), "first item");
            Ensure(ambient.Value == "caller", "Current/await continuation AsyncLocal change escaped");
            Ensure(stream.CurrentAmbient == "move", "producer execution context lost across pending await");
            SynchronizationContext.SetSynchronizationContext(callerContext);
            var dispose = iterator.DisposeAsync();
            Ensure(ambient.Value == "caller", "Dispose AsyncLocal change escaped");
            Ensure(ReferenceEquals(SynchronizationContext.Current, callerContext), "Dispose SyncContext change escaped");
            SynchronizationContext.SetSynchronizationContext(previousContext);
            await dispose.ConfigureAwait(false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            stream.Release.TrySetResult(true);
            await iterator.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DisposingUnstartedEnumeratorDoesNotClaimSingleEnumeration()
    {
        var source = new ScriptedStream();
        var stream = Wrap(source);
        var unused = stream.GetAsyncEnumerator();
        await unused.DisposeAsync();
        Ensure(source.GetEnumeratorCount == 0, "unstarted dispose must not enter source");
        var active = stream.GetAsyncEnumerator();
        Ensure(await active.MoveNextAsync(), "later enumerator may claim stream");
        var duplicate = stream.GetAsyncEnumerator();
        ValueTask<bool> duplicateMove;
        try { duplicateMove = duplicate.MoveNextAsync(); }
        catch { throw new InvalidOperationException("duplicate enumeration must fault returned ValueTask, not throw synchronously"); }
        Ensure(await Failure(duplicateMove) is InvalidOperationException, "duplicate enumeration rejection");
        await active.DisposeAsync();
        await duplicate.DisposeAsync();
        Ensure(source.DisposeCount == 1, "only claimed source is disposed");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CurrentAndDisposalFailuresKeepIdentityAndPrecedence(bool failCurrent, bool failDispose)
    {
        var sourceFailure = new InvalidOperationException("source identity");
        var disposeFailure = new ApplicationException("dispose identity");
        var source = new ScriptedStream
        {
            MoveResult = failCurrent,
            CurrentFailure = sourceFailure,
            DisposeFailure = failDispose ? disposeFailure : null
        };
        var iterator = Wrap(source).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        if (failCurrent || failDispose)
            Ensure(ReferenceEquals(await Failure(move), failDispose ? disposeFailure : sourceFailure), "failure identity/precedence");
        else
            Ensure(!await move, "empty stream");
        Ensure(source.CurrentCount == (failCurrent ? 1 : 0), "false MoveNext must not read Current");
        Ensure(source.DisposeCount == 1, "terminal disposal exactly once");
        await iterator.DisposeAsync();
        Ensure(source.DisposeCount == 1, "post-terminal Dispose is idempotent");
    }

    [Test]
    public async Task PendingMoveRejectsConcurrentDisposeThenConsumesSourceOnce()
    {
        var source = new SourceBackedStream();
        var iterator = Wrap(source).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        Ensure(!move.IsCompleted, "pending move");
        Exception? rejection = null;
        try { _ = iterator.DisposeAsync(); }
        catch (Exception failure) { rejection = failure; }
        Ensure(rejection is NotSupportedException, "compiler iterator rejects disposal during active move");
        source.Complete(true);
        Ensure(await move, "pending source item");
        Ensure(source.GetResultCount == 1, "single source consumption");
        await iterator.DisposeAsync();
        Ensure(source.DisposeCount == 1, "ordinary dispose after consumption");
    }

    [Test]
    public async Task PendingDisposalOverridesCurrentFailureWithoutEarlyCompletion()
    {
        var sourceFailure = new InvalidOperationException("current");
        var disposalFailure = new ApplicationException("dispose");
        var dispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedStream { CurrentFailure = sourceFailure, PendingDispose = dispose.Task };
        var iterator = Wrap(source).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        Ensure(!move.IsCompleted, "ordinary error awaits disposal");
        dispose.SetException(disposalFailure);
        Ensure(ReferenceEquals(await Failure(move), disposalFailure), "asynchronous disposal wins without wrapping");
        await iterator.DisposeAsync();
    }

    [Test]
    public async Task ApplicationCancellationIsCanceledAndRetainsIdentity()
    {
        var cancellation = new OperationCanceledException("application cancellation");
        var source = new ScriptedStream { MoveFailure = cancellation };
        var iterator = Wrap(source).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        var task = move.AsTask();
        Exception? observed = null;
        try { _ = await task; }
        catch (Exception failure) { observed = failure; }
        Ensure(task.IsCanceled, "OCE completion must remain canceled, not faulted");
        Ensure(ReferenceEquals(cancellation, observed), "unassociated OCE retains exact identity");
        await iterator.DisposeAsync();
    }

    [Test]
    public async Task ReentrantDisposeDuringSourceGetEnumeratorIsRejected()
    {
        var source = new ReentrantSetupStream();
        var iterator = Wrap(source).GetAsyncEnumerator();
        source.DisposeOuter = () => iterator.DisposeAsync();
        Exception? moveFailure = null;
        try { _ = await iterator.MoveNextAsync(); }
        catch (Exception failure) { moveFailure = failure; }
        Ensure(source.Rejection is NotSupportedException, "executing setup must reject reentrant Dispose");
        Ensure(moveFailure is null, "rejected reentrant disposal must not corrupt current move");
        await iterator.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExceptionFilterObservesCancellationBeforeProducerFinally(bool throwFromCurrent)
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("first-pass filter identity");
        var source = new FinallyCancellingStream(cancellation, original, throwFromCurrent);
        var iterator = Wrap(source, cancellation.Token).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        var completedBeforeDisposalRelease = move.IsCompleted;
        Exception observed;
        try
        {
            await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { source.DisposeRelease.TrySetResult(); }
        observed = await Failure(move);
        Ensure(!completedBeforeDisposalRelease, "first-pass unassociated failure must await ordinary disposal");
        Ensure(ReferenceEquals(observed, original), "late finally cancellation must not replace original with ownership carrier");
        await iterator.DisposeAsync();
    }

    private sealed class ReentrantSetupStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal Func<ValueTask> DisposeOuter = null!;
        internal Exception? Rejection;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            try { _ = DisposeOuter(); }
            catch (Exception failure) { Rejection = failure; }
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => default;
    }

    private sealed class FinallyCancellingStream(CancellationTokenSource cancellation, Exception original, bool throwFromCurrent)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => throwFromCurrent ? ThrowAndCancel() : 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => throwFromCurrent ? ValueTask.FromResult(true) : ValueTask.FromResult(ThrowAndCancel() != 0);
        private int ThrowAndCancel()
        {
            try { throw original; }
            finally { cancellation.Cancel(); }
        }
        public ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeRelease.Task);
        }
    }

    [Test]
    public async Task ReusedSourcePreservesTokensAndCurrentAcrossPendingMoves()
    {
        var source = new SourceBackedStream();
        var iterator = Wrap(source).GetAsyncEnumerator();
        for (var index = 1; index <= 3; index++)
        {
            if (index != 1) source.Reset();
            source.Value = index;
            var move = iterator.MoveNextAsync();
            Ensure(!move.IsCompleted, "each source move must suspend");
            var currentWhilePending = iterator.Current;
            source.Complete(index != 3);
            Ensure(await move == (index != 3), "source result consumed exactly once per version");
            Ensure(currentWhilePending == 0, "compiler iterator clears Current when resuming after yield");
        }
        Ensure(source.GetResultCount == 3, "three versions consumed once each");
        Ensure(iterator.Current == 0, "terminal clears Current");
        Ensure(source.DisposeCount == 1, "terminal disposes once");
        await iterator.DisposeAsync();
    }

    [Test]
    public async Task PendingInvocationRetainsEntryExecutionContext()
    {
        var ambient = new AsyncLocal<string?> { Value = "entry" };
        var source = new ContextStream(ambient, false);
        var invocation = new TaskCompletionSource<SharpLinkClientInvocationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var iterator = WrapInvocation(new ValueTask<SharpLinkClientInvocationResult>(invocation.Task)).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        Ensure(!move.IsCompleted, "invocation setup suspended");
        ambient.Value = "caller-after-entry";
        invocation.SetResult(new SharpLinkClientInvocationResult(source));
        Ensure(await move, "pending invocation item");
        Ensure(source.GetEnumeratorAmbient == "entry", "initial await captures entry context");
        Ensure(ambient.Value == "caller-after-entry", "resumed setup must not mutate caller context");
        await iterator.DisposeAsync();
    }

    [Test]
    public async Task DeadlineFilterObservesClockBeforeProducerFinally()
    {
        var clock = new MutableClock();
        var original = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "application deadline");
        var source = new FinallyAdvancingStream(clock, original);
        var iterator = WrapInvocation(ValueTask.FromResult(new SharpLinkClientInvocationResult(source)),
            deadline: RpcDeadline.Create(TimeSpan.FromSeconds(1), clock), timeProvider: clock).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync();
        var completedBeforeDisposalRelease = move.IsCompleted;
        try { await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { source.DisposeRelease.TrySetResult(); }
        var observed = await Failure(move);
        Ensure(!completedBeforeDisposalRelease, "late finally clock advance must not cause physical transfer");
        Ensure(ReferenceEquals(observed, original), "application deadline retains original identity");
        await iterator.DisposeAsync();
    }

    [Test]
    public async Task PendingDisposeRestoresCallerExecutionAndSynchronizationContext()
    {
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var source = new PendingContextDisposeStream(ambient);
        var iterator = Wrap(source).GetAsyncEnumerator();
        Ensure(await iterator.MoveNextAsync(), "item");
        var previousContext = SynchronizationContext.Current;
        var context = new SynchronizationContext();
        ValueTask disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            disposal = iterator.DisposeAsync();
            Ensure(!disposal.IsCompleted, "dispose suspended");
            Ensure(ambient.Value == "caller", "pending Dispose AsyncLocal mutation escaped");
            Ensure(ReferenceEquals(SynchronizationContext.Current, context), "pending Dispose SyncContext mutation escaped");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            source.Release.TrySetResult();
        }
        await disposal.ConfigureAwait(false);
        Ensure(ambient.Value == "caller", "dispose continuation mutated caller");
    }

    private sealed class MutableClock : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Timestamp;
    }

    private sealed class FinallyAdvancingStream(MutableClock clock, Exception original) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            try { throw original; }
            finally { clock.Timestamp = 10; }
        }
        public ValueTask DisposeAsync() { DisposeEntered.TrySetResult(); return new(DisposeRelease.Task); }
    }

    private sealed class PendingContextDisposeStream(AsyncLocal<string?> ambient) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 42;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(true);
        public ValueTask DisposeAsync()
        {
            ambient.Value = "dispose";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            return new(Release.Task);
        }
    }

    private static async Task<Exception> Failure(ValueTask<bool> move)
    {
        try { _ = await move; }
        catch (Exception failure) { return failure; }
        throw new InvalidOperationException("expected failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ContextStream(AsyncLocal<string?> ambient, bool pending) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? CurrentAmbient, GetEnumeratorAmbient;
        public int Current { get { CurrentAmbient = ambient.Value; ambient.Value = "current"; return 42; } }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            GetEnumeratorAmbient = ambient.Value;
            ambient.Value = "get";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            ambient.Value = "move";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            return pending ? new ValueTask<bool>(Release.Task) : ValueTask.FromResult(true);
        }
        public ValueTask DisposeAsync()
        {
            ambient.Value = "dispose";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            return default;
        }
    }

    private sealed class ScriptedStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal bool MoveResult = true;
        internal Exception? MoveFailure, CurrentFailure, DisposeFailure;
        internal Task? PendingDispose;
        internal int GetEnumeratorCount, CurrentCount, DisposeCount;
        public int Current { get { CurrentCount++; if (CurrentFailure is { } e) throw e; return 42; } }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) { GetEnumeratorCount++; return this; }
        public ValueTask<bool> MoveNextAsync() => MoveFailure is { } e ? ValueTask.FromException<bool>(e) : ValueTask.FromResult(MoveResult);
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeFailure is { } e) throw e;
            return PendingDispose is { } task ? new ValueTask(task) : default;
        }
    }

    private sealed class SourceBackedStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>, IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };
        internal int GetResultCount, DisposeCount;
        internal int Value = 42;
        public int Current => Value;
        internal void Reset() => _source.Reset();
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(this, _source.Version);
        internal void Complete(bool value) => _source.SetResult(value);
        public ValueTask DisposeAsync() { DisposeCount++; return default; }
        public bool GetResult(short token) { GetResultCount++; return _source.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}
