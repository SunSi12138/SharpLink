namespace SharpLink.UnitTests.Runtime;

public sealed class InboundChildPhysicalCleanupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task CapturedMailboxWaitsForOutgoingTailThenActualChildDisposeAfterPeerTerminal()
    {
        var manager = Create(41, 1);
        var child = new HeldChild();
        manager.Register(41, 1, child);
        var outgoing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = manager.AbandonRequestStreamsAfterDispatchesAsync(41, 1, outgoing.Task).AsTask();
        manager.CompletePeerStream(41, 1, null);
        Ensure(!child.DisposeEntered.Task.IsCompleted && !physical.IsCompleted && manager.ActiveStreamCount == 1,
            "existing outgoing tail protects live typed input; peer EOF alone cannot begin local disposal");
        outgoing.SetResult();
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!physical.IsCompleted, "IsDetached self-wait cut is not physical Dispose completion");
        child.ReleaseDispose.SetResult();
        await physical.WaitAsync(Timeout);
        Ensure(child.DisposeCount == 1 && child.DrainedCount == 1 && manager.ActiveStreamCount == 0,
            "captured operation joins actual Dispose/callback then releases exact peer-terminal route");
    }

    [Test]
    public async Task SessionRouteRemovalWhileOutgoingHeldCannotDiscardCapturedChildOwner()
    {
        var manager = Create(48, 1);
        var child = new HeldChild();
        manager.Register(48, 1, child);
        var outgoing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = manager.CaptureRequestStreamsForCleanup(48, 1);
        var physical = manager.AbandonCapturedRequestStreamsAsync(ref capture, outgoing.Task).AsTask();
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Session closes while outgoing tail is held"));
        Ensure(manager.ActiveStreamCount == 0 && child.DisposeCount == 0 && !physical.IsCompleted,
            "removed map entry cannot start child disposal before the captured outgoing prerequisite");
        outgoing.SetResult();
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!physical.IsCompleted, "captured child remains owned after map removal and detached cut");
        child.ReleaseDispose.SetResult();
        await physical.WaitAsync(Timeout);
        Ensure(child.DisposeCount == 1 && child.DrainedCount == 1, "actual detached child tail joins exactly once");
    }

    [Test]
    public async Task PeerTerminalDuringHeldDisposeDoesNotReleaseServiceFacingJoin()
    {
        var manager = Create(42, 1);
        var child = new HeldChild();
        manager.Register(42, 1, child);
        var physical = manager.AbandonRequestStreamsAfterDispatchesAsync(42, 1).AsTask();
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(manager.ActiveStreamCount == 1, "host discard route remains until real peer terminal");
        manager.CompletePeerStream(42, 1, null);
        Ensure(manager.ActiveStreamCount == 0 && !physical.IsCompleted,
            "route removal cannot refund the still-running typed Dispose owner");
        child.ReleaseDispose.SetResult();
        await physical.WaitAsync(Timeout);
        Ensure(child.DisposeCount == 1 && child.DrainedCount == 1, "physical terminal tail joined once");
    }

    [Test]
    public async Task FirstDisposalFailureCannotSkipAnotherDeclaredChildPhysicalTail()
    {
        var manager = Create(43, 2);
        var first = new HeldChild(throwOnDispose: true);
        var second = new HeldChild();
        manager.Register(43, 1, first);
        manager.Register(43, 2, second);
        var physical = manager.AbandonRequestStreamsAfterDispatchesAsync(43, 2).AsTask();
        await Task.WhenAll(first.DisposeEntered.Task, second.DisposeEntered.Task).WaitAsync(Timeout);
        first.ReleaseDispose.SetResult();
        Ensure(!physical.IsCompleted, "faulting first tail does not skip the held second tail");
        second.ReleaseDispose.SetResult();
        Exception? failure = null;
        try { await physical.WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { failure = error; }
        Ensure(failure is not null && first.DisposeCount == 1 && second.DisposeCount == 1 &&
            first.DrainedCount == 1 && second.DrainedCount == 1, "both exact cleanup owners finish before fault surfaces");
        manager.CompletePeerStream(43, 1, null);
        manager.CompletePeerStream(43, 2, null);
        Ensure(manager.ActiveStreamCount == 0, "both discard routes close only on peer terminals");
    }

    [Test]
    public async Task NeverAttachedMailboxPhysicalCleanupHasNoDetachedWaitCycle()
    {
        var manager = Create(44, 2);
        await manager.AbandonRequestStreamsAfterDispatchesAsync(44, 2).AsTask().WaitAsync(Timeout);
        Ensure(manager.ActiveStreamCount == 2, "no-child cleanup leaves bounded peer-discard routes");
        manager.CompletePeerStream(44, 1, null);
        manager.CompletePeerStream(44, 2, null);
        Ensure(manager.ActiveStreamCount == 0, "real peer terminal drains no-child routes");
    }

    [Test]
    public async Task CopiedCaptureExecuteRaceHasExactlyOnePhysicalCleanupOwner()
    {
        var manager = Create(45, 1);
        var child = new HeldChild();
        manager.Register(45, 1, child);
        var capture = manager.CaptureRequestStreamsForCleanup(45, 1);
        var first = capture;
        var second = capture;
        using var start = new Barrier(2);
        (Task? Cleanup, Exception? Failure) Execute(StreamManager.CapturedRequestStreamCleanup copy)
        {
            Ensure(start.SignalAndWait(Timeout), "both ownership contenders reached the barrier");
            try { return (manager.AbandonCapturedRequestStreamsAsync(ref copy).AsTask(), null); }
            catch (Exception error) { return (null, error); }
        }
        var attempts = await Task.WhenAll(Task.Run(() => Execute(first)), Task.Run(() => Execute(second))).WaitAsync(Timeout);
        var winner = attempts[0].Cleanup ?? attempts[1].Cleanup;
        var loser = attempts[0].Failure ?? attempts[1].Failure;
        Ensure(winner is not null && loser is InvalidOperationException &&
            (attempts[0].Cleanup is null) != (attempts[1].Cleanup is null),
            "shared first-entry claim, not value-copy convention, gives one executable owner");
        capture.Dispose(); // Losing copy cannot release the winner's acquired entries.
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!winner!.IsCompleted && child.DisposeCount == 1, "losing Dispose cannot finish held actual cleanup");
        child.ReleaseDispose.SetResult();
        await winner.WaitAsync(Timeout);
        manager.CompletePeerStream(45, 1, null);
        Ensure(manager.ActiveStreamCount == 0 && child.DrainedCount == 1, "exact route and typed child retire once");
    }

    [Test]
    public async Task CopiedCaptureDisposeExecuteRaceCannotDoubleReleaseEntries()
    {
        var manager = Create(46, 1);
        var child = new HeldChild();
        manager.Register(46, 1, child);
        var capture = manager.CaptureRequestStreamsForCleanup(46, 1);
        var executeCopy = capture;
        var disposeCopy = capture;
        using var start = new Barrier(2);
        var execute = Task.Run(() =>
        {
            Ensure(start.SignalAndWait(Timeout), "both ownership contenders reached the barrier");
            try { return (Cleanup: (Task?)manager.AbandonCapturedRequestStreamsAsync(ref executeCopy).AsTask(), Failure: (Exception?)null); }
            catch (Exception error) { return (Cleanup: (Task?)null, Failure: (Exception?)error); }
        });
        var dispose = Task.Run(() => { Ensure(start.SignalAndWait(Timeout), "both ownership contenders reached the barrier"); disposeCopy.Dispose(); });
        var result = await execute.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);
        capture.Dispose();
        Task cleanup;
        if (result.Cleanup is { } accepted)
        {
            Ensure(result.Failure is null, "execute winner owns physical cleanup");
            cleanup = accepted;
        }
        else
        {
            Ensure(result.Failure is InvalidOperationException && child.DisposeCount == 0 && manager.ActiveStreamCount == 1,
                "pre-commit Dispose releases only capture; normal service cleanup remains required");
            cleanup = manager.AbandonRequestStreamsAfterDispatchesAsync(46, 1).AsTask();
        }
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        child.ReleaseDispose.SetResult();
        await cleanup.WaitAsync(Timeout);
        manager.CompletePeerStream(46, 1, null);
        Ensure(child.DisposeCount == 1 && child.DrainedCount == 1 && manager.ActiveStreamCount == 0,
            "either winner preserves one real child cleanup, no shared-entry underflow");
    }

    [Test]
    public void DefaultCaptureAndSynchronousNoChildCleanupNeedNoScheduledContinuation()
    {
        var manager = Create(47, 1);
        StreamManager.CapturedRequestStreamCleanup empty = default;
        Ensure(manager.AbandonCapturedRequestStreamsAsync(ref empty).IsCompletedSuccessfully, "default execute is synchronous no-op");
        empty.Dispose();
        var capture = manager.CaptureRequestStreamsForCleanup(47, 1);
        var operation = manager.AbandonCapturedRequestStreamsAsync(ref capture);
        Ensure(operation.IsCompletedSuccessfully, "no-child cleanup does not ForceYield or allocate a pending completion");
        operation.GetAwaiter().GetResult();
        capture.Dispose();
        manager.CompletePeerStream(47, 1, null);
        Ensure(manager.ActiveStreamCount == 0, "no-child route finishes at real peer terminal");
    }

    [Test]
    public void InvocationCaptureAfterSessionRemovalRejectsBeforeUserEntry()
    {
        var manager = Create(51, 2);
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed before invocation capture"));
        var userEntries = 0;
        var accepted = manager.TryCaptureRequestStreamsForInvocation(51, 2, out var capture);
        if (accepted) userEntries++;
        capture.Dispose();
        Ensure(!accepted && userEntries == 0 && manager.ActiveStreamCount == 0,
            "missing terminal routes cannot be treated as an empty successful invocation owner");
    }

    [Test]
    public async Task InvocationCaptureBeforeSessionRemovalRetainsOriginalPhysicalChild()
    {
        var manager = Create(52, 2);
        Ensure(manager.TryCaptureRequestStreamsForInvocation(52, 2, out var capture),
            "full exact declared domain is captured before service factory or typed attachment");
        var child = new HeldChild();
        manager.Register(52, 1, child);
        var outgoing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "closed after invocation capture"));
        Ensure(manager.ActiveStreamCount == 0 && child.DisposeCount == 0,
            "original capture protects subsequently attached child despite map removal");
        var physical = manager.AbandonCapturedRequestStreamsAsync(ref capture, outgoing.Task).AsTask();
        Ensure(!physical.IsCompleted && child.DisposeCount == 0, "actual outgoing tail precedes local input disposal");
        outgoing.SetResult();
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!physical.IsCompleted, "physical Dispose, not map absence or detached cut, owns completion");
        child.ReleaseDispose.SetResult();
        await physical.WaitAsync(Timeout);
        capture.Dispose();
        Ensure(child.DisposeCount == 1 && child.DrainedCount == 1, "original child physical owner joins once");
    }

    [Test]
    public async Task MissingDeclaredRouteRollsBackPartialInvocationCapture()
    {
        var manager = Create(53, 1);
        Ensure(!manager.TryCaptureRequestStreamsForInvocation(53, 2, out var refused),
            "one present route is not acceptance of the two-route declared domain");
        refused.Dispose();
        Ensure(manager.TryCaptureRequestStreamsForInvocation(53, 1, out var capture),
            "failed partial capture does not close or replace the original route");
        var child = new HeldChild();
        manager.Register(53, 1, child);
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "drain exact retained route"));
        var physical = manager.AbandonCapturedRequestStreamsAsync(ref capture).AsTask();
        await child.DisposeEntered.Task.WaitAsync(Timeout);
        child.ReleaseDispose.SetResult();
        await physical.WaitAsync(Timeout);
        Ensure(child.DrainedCount == 1 && child.DisposeCount == 1,
            "no leaked partial acquisition can strand the actual child dispatch drain");
    }

    [Test]
    public void AbandonedOrMissingDomainCannotSatisfyStrictInvocationCapture()
    {
        var manager = Create(54, 1);
        manager.AbandonExistingRequestStreams(54, 1);
        Ensure(!manager.TryCaptureRequestStreamsForInvocation(54, 1, out var abandoned),
            "present but locally closed mailbox cannot admit user invocation");
        abandoned.Dispose();
        Ensure(!manager.TryCaptureRequestStreamsForInvocation(55, 1, out var missing),
            "absent request domain is refusal, not default success");
        missing.Dispose();
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "cleanup"));
        Ensure(manager.ActiveStreamCount == 0, "refused domain leaves no phantom route charge");
    }

    [Test]
    public Task InvocationOwnerCapturePreservesBufferedPeerErrorForTypedConsumer()
        => CheckRetainedPeerTerminalAsync(new InvalidOperationException("exact-peer-error"));

    [Test]
    public Task InvocationOwnerCapturePreservesBufferedPeerEofForTypedConsumer()
        => CheckRetainedPeerTerminalAsync(null);

    private static async Task CheckRetainedPeerTerminalAsync(Exception? terminal)
    {
        var manager = Create(56, 1);
        await manager.DispatchChunkAsync(56, 1, new ReadOnlySequence<byte>(new byte[] { 7, 8 }));
        manager.CompletePeerStream(56, 1, terminal);
        var accepted = manager.TryCaptureRequestStreamsForInvocation(56, 1, out var capture);
        Ensure(manager.ActiveStreamCount == 1 && accepted,
            "already accepted buffered peer EOF/Error remains an owned invocation input, not new admission");
        var child = new TerminalObservingChild();
        manager.Register(56, 1, child);
        Ensure(child.Bytes == 2 && child.CompleteCount == 1 && ReferenceEquals(child.Terminal, terminal),
            "exact buffered payload and original terminal reach the future typed consumer unchanged");
        await manager.AbandonCapturedRequestStreamsAsync(ref capture).AsTask().WaitAsync(Timeout);
        capture.Dispose();
        Ensure(manager.ActiveStreamCount == 0 && child.DisposeCount == 1 && child.DrainedCount == 1,
            "original peer-terminal route and typed physical owner retire once");
    }

    [Test]
    public void SessionCloseStillRejectsInvocationOwnerCaptureAfterPeerErrorAndEof()
    {
        var manager = Create(57, 2);
        manager.CompletePeerStream(57, 1, new InvalidOperationException("peer-error-before-close"));
        manager.CompletePeerStream(57, 2, null);
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Session closed before invocation entry"));
        Ensure(!manager.TryCaptureRequestStreamsForInvocation(57, 2, out var capture),
            "allowing stored peer terminal never bypasses the separate Session close rejection");
        capture.Dispose();
        Ensure(manager.ActiveStreamCount == 0, "closed Session retains no accepted invocation domain");
    }

    private sealed class TerminalObservingChild : IStreamDispatcher, IStreamDispatchLease, IAsyncDisposable
    {
        internal long Bytes;
        internal int CompleteCount;
        internal Exception? Terminal;
        internal int DisposeCount;
        internal int DrainedCount;
        public void BindDispatchState(IStreamDispatchState state) { }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => DispatchAsync(payload);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) { Bytes += payload.Length; return ValueTask.CompletedTask; }
        public void Complete(bool isError, string? errorMessage) => throw new InvalidOperationException("typed terminal must use exact exception overload");
        public void Complete(Exception? exception) { CompleteCount++; Terminal = exception; }
        public void OnDispatchesDrained() => DrainedCount++;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private static StreamManager Create(long id, int count)
    {
        var manager = new StreamManager();
        Ensure(manager.TryReservePreAdmissionStreams(id, count, RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true), "whole declared domain reserved");
        return manager;
    }

    private sealed class HeldChild(bool throwOnDispose = false) : IStreamDispatcher, IStreamDispatchLease, IAsyncDisposable
    {
        private IStreamDispatchState? _state;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount;
        internal int DrainedCount;
        public void BindDispatchState(IStreamDispatchState state) => _state = state;
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void OnDispatchesDrained() => DrainedCount++;
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeEntered.TrySetResult();
            // Mirrors the existing pooled-child self-wait: detached must be published BEFORE
            // awaiting this operation, but the separate physical join must remain pending.
            await _state!.WaitForDetachedAsync(CancellationToken.None);
            await ReleaseDispose.Task;
            if (throwOnDispose) throw new InvalidOperationException("injected physical child disposal failure");
        }
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
