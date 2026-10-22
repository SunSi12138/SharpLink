namespace SharpLink.UnitTests.Client;

public class AbandonedClientNestedOwnershipTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task ConsumedNestedTerminalCompletesDescendantBeforeDisposal(bool deadline, bool taskCanceled)
    {
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception terminal = deadline
            ? new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, 23, "nested deadline")
            : taskCanceled
                ? new TaskCanceledException("independently canceled nested task", null, new CancellationToken(true))
                : new OperationCanceledException("independently canceled nested operation", new CancellationToken(true));
        var stream = new NestedStream(terminal);
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(new Interceptor(stream));
        SharpLinkPhysicalWork.TrackAbandonedUserWork(terminal, child.Task);
        try
        {
            Exception? observed = null;
            try
            {
                await AbandonedClientOwnershipTests.Invoke(
                    fixture.Channel, RpcMethodKind.ServerStreaming, timed: false).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            Ensure(deadline
                ? observed is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                : observed is OperationCanceledException, "original terminal kind must be preserved");
            Ensure(observed!.GetType() == terminal.GetType(), "standard exception concrete type must be preserved");
            Ensure(observed.Message == terminal.Message && HasCause(observed, terminal), "fresh carriers preserve message and original cause");
            if (observed is SharpLinkException observedDeadline)
                Ensure(observedDeadline.DetailCode == 23, "deadline detail code survives both ownership boundaries");
            if (terminal is OperationCanceledException originalCancellation)
                Ensure(((OperationCanceledException)observed).CancellationToken == originalCancellation.CancellationToken,
                    "cancellation token identity must be preserved");
            fixture.Module.TryBeginDraining();
            Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "nested physical child retains the module");
            Ensure(!stream.Disposed, "Dispose cannot overlap the nested physical child");
            child.TrySetResult();
            await fixture.Module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stream.Disposed, "Dispose completes before the module is released");
        }
        finally
        {
            child.TrySetResult();
        }
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task IteratorBoundaryTerminalTransfersLifetimeWithoutDelayingPublicFailure(int boundary, bool deadline)
    {
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception original = deadline
            ? new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "nested boundary deadline")
            : new OperationCanceledException("nested boundary cancellation", new CancellationToken(true));
        SharpLinkPhysicalWork.TrackAbandonedUserWork(original, child.Task);
        var stream = new BoundaryStream(boundary, original);
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(new Interceptor(stream));
        try
        {
            Exception? terminal = null;
            try { await AbandonedClientOwnershipTests.Invoke(fixture.Channel, RpcMethodKind.ServerStreaming, timed: false).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { terminal = exception; }
            Ensure(terminal is not TimeoutException && terminal?.GetType() == original.GetType(), "selected nested terminal must stay prompt");
            fixture.Module.TryBeginDraining();
            Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "boundary descendant retains the module");
            Ensure(IsAlive(stream.Token), "boundary descendant retains its linked source");
            if (boundary == 1)
                Ensure(stream.DisposeCalls == 0, "Current failure descendant precedes Dispose");
            child.TrySetResult();
            await fixture.Module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!IsAlive(stream.Token), "source releases after physical child");
            Ensure(stream.DisposeCalls == (boundary == 0 ? 0 : 1), "iterator is disposed exactly once when acquired");
        }
        finally { child.TrySetResult(); }
    }

    [Test]
    public async Task DynamicNormalDisposalDoesNotCaptureSynchronizationContext()
    {
        var stream = new DeferredDisposalStream();
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(new Interceptor(stream));
        var channel = fixture.Channel;
        var method = new RpcMethodDescriptor(1, 929, RpcMethodKind.ServerStreaming, true, false, false, null);
        var request = default(RpcEmptyRequest);
        var enumerator = channel.InvokeServerStreamingAsync(method, request, RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(), null).GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync(), "first local item");
        var context = new UnpumpedContext();
        var previous = SynchronizationContext.Current;
        Task disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            disposal = enumerator.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        stream.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(context.Posts == 0, "normal dynamic stream disposal must remain ConfigureAwait(false)");
    }

    [Test]
    public async Task CompletedPhysicalOwnerCannotInlineBlockingCombinedSourceDisposal()
    {
        using var caller = new CancellationTokenSource();
        var interceptor = new CompletedOwnerInterceptor();
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(interceptor);
        await LongRunningTestWorker.Run(() =>
        {
            Task? cancellation = null;
            Task? call = null;
            try
            {
                call = AbandonedClientOwnershipTests.Invoke(fixture.Channel, RpcMethodKind.Unary, caller.Token, timed: false);
                LongRunningTestWorker.WaitForPhase(interceptor.Entered.Task, TimeSpan.FromSeconds(5));
                cancellation = LongRunningTestWorker.Run(() => caller.Cancel());
                LongRunningTestWorker.WaitForPhase(interceptor.CallbackEntered.Task, TimeSpan.FromSeconds(5));
                interceptor.Release.TrySetResult();
                Exception? failure = null;
                try { LongRunningTestWorker.WaitForPhase(call, TimeSpan.FromSeconds(5)); }
                catch (Exception exception) { failure = exception; }
                Ensure(failure is OperationCanceledException, "completed owner handoff must not block the public terminal");
                fixture.Module.TryBeginDraining();
                Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "combined cancellation propagation retains the module");
                interceptor.CallbackRelease.Set();
                LongRunningTestWorker.WaitForPhase(Task.WhenAll(cancellation, fixture.Module.WaitForDrainAsync()), TimeSpan.FromSeconds(5));
            }
            finally
            {
                interceptor.Release.TrySetResult();
                interceptor.CallbackRelease.Set();
                if (cancellation is not null)
                    LongRunningTestWorker.Join(cancellation, TimeSpan.FromSeconds(5));
                if (call is not null)
                    LongRunningTestWorker.Join(call, TimeSpan.FromSeconds(5));
                interceptor.Registration.Dispose();
                fixture.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed class CompletedOwnerInterceptor : ISharpLinkClientInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim CallbackRelease { get; } = new(false);
        internal CancellationTokenRegistration Registration;
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            var terminal = new OperationCanceledException(context.CancellationToken);
            var owner = SharpLinkPhysicalWork.CreateOwner(terminal);
            owner.Track(Task.CompletedTask);
            await owner.Completion;
            Registration = context.CancellationToken.Register(() => { CallbackEntered.TrySetResult(); CallbackRelease.Wait(); });
            Entered.TrySetResult();
            await Release.Task;
            throw terminal;
        }
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    public async Task UnassociatedApplicationFailurePreservesIdentity(RpcMethodKind kind)
    {
        var original = new ApplicationFailure();
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(new FailureInterceptor(original));
        Exception? observed = null;
        try { await AbandonedClientOwnershipTests.Invoke(fixture.Channel, kind, timed: false); }
        catch (Exception exception) { observed = exception; }
        Ensure(ReferenceEquals(observed, original), "ordinary application failures are not wrapped by physical ownership");
        fixture.Module.TryBeginDraining();
        await fixture.Module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ApplicationFailure : Exception;
    private sealed class FailureInterceptor(Exception exception) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
            => ValueTask.FromException<SharpLinkClientInvocationResult>(exception);
    }

    private static bool HasCause(Exception observed, Exception original)
    {
        for (Exception? cause = observed; cause is not null; cause = cause.InnerException)
            if (ReferenceEquals(cause, original)) return true;
        return false;
    }

    private static bool IsAlive(CancellationToken token)
    {
        try { _ = token.WaitHandle; return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private sealed class BoundaryStream(int boundary, Exception terminal) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal CancellationToken Token;
        internal int DisposeCalls;
        public int Current => throw terminal;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (boundary == 0) throw terminal;
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(boundary == 1);
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (boundary == 2) throw terminal;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredDisposalStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => new(Release.Task);
    }

    private sealed class UnpumpedContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }

    private sealed class Interceptor(IAsyncEnumerable<int> stream) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
    }

    private sealed class NestedStream(Exception terminal) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal bool Disposed;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(terminal);
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
