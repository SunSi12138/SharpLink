using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

// Real StreamManager entry removal/drain, not a replacement ownership model.
public sealed class StreamManagerPhysicalDrainFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingCompleteStillReturnsExactHeldDispatchJoin(bool canceledFailure)
    {
        Exception original = canceledFailure
            ? new OperationCanceledException("raw completion failure")
            : new InvalidOperationException("raw completion failure");
        var finalized = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null,
            (_, _) => finalized++);
        var dispatcher = new HeldDispatcher { CompleteFailure = original };
        manager.Register(401, 0, dispatcher);
        var dispatch = manager.DispatchChunkAsync(401, 0, ReadOnlySequence<byte>.Empty).AsTask();
        await dispatcher.Entered.Task.WaitAsync(Timeout);
        Task? drain = null;
        try
        {
            // A synchronous throw here loses the only exact removed-entry drain owner.
            drain = manager.CompleteStreamAfterDispatchesAsync(401, 0, null).AsTask();
            Ensure(!drain.IsCompleted && manager.ActiveStreamCount == 0,
                "removed route stops intake but its acquired dispatch remains owned");
            Ensure(!dispatcher.State!.IsDetached && finalized == 0,
                "no final credit/detach before original dispatch exits");
            manager.CompleteAll(null);
            Ensure(!drain.IsCompleted, "global drain cannot replace the removed entry's captured join");
        }
        finally { dispatcher.Release.TrySetResult(); }
        await dispatch.WaitAsync(Timeout);
        var error = await Failure(drain!);
        Ensure(drain!.IsFaulted && ReferenceEquals(PhysicalTaskFailureException.GetRaw(error), original),
            "raw Complete exception including OCE must survive as a fault after actual join");
        Ensure(dispatcher.State!.IsDetached && dispatcher.Drained == 1 && finalized == 1,
            "exact entry detaches and finalizes once despite Complete failure");
    }

    [Test]
    public async Task EveryFinalizationObligationRunsAfterJoinDespiteIndependentFailures()
    {
        var complete = new InvalidOperationException("complete");
        var clear = new InvalidOperationException("clear credit callback");
        var terminal = new InvalidOperationException("terminal callback");
        var detach = new InvalidOperationException("detach callback");
        var terminalCalls = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null,
            (_, _) => { terminalCalls++; throw terminal; });
        var dispatcher = new HeldDispatcher
        {
            CompleteFailure = complete, ClearFailure = clear, DetachFailure = detach
        };
        manager.Register(402, 0, dispatcher);
        var dispatch = manager.DispatchChunkAsync(402, 0, ReadOnlySequence<byte>.Empty).AsTask();
        await dispatcher.Entered.Task.WaitAsync(Timeout);
        var drain = manager.CompleteStreamAfterDispatchesAsync(402, 0, null).AsTask();
        try { Ensure(!drain.IsCompleted, "all failures wait for actual acquired dispatch"); }
        finally { dispatcher.Release.TrySetResult(); }
        await dispatch.WaitAsync(Timeout);
        var raw = PhysicalTaskFailureException.GetRaw(await Failure(drain));
        Ensure(Count(raw, complete) == 1 && Count(raw, clear) == 1 &&
               Count(raw, terminal) == 1 && Count(raw, detach) == 1,
            "each independent original exception is retained exactly once");
        Ensure(dispatcher.ClearCalls == 1 && terminalCalls == 1 && dispatcher.Drained == 1 &&
               dispatcher.State!.IsDetached && manager.ActiveStreamCount == 0,
            "clear failure cannot skip terminal; terminal failure cannot skip detach/accounting");
        manager.CompleteAll(null);
        Ensure(dispatcher.CompleteCalls == 1 && dispatcher.Drained == 1,
            "removed owner is not spuriously completed again by later global cleanup");
    }

    [Test]
    public async Task IdleCallbackFailureIsReturnedRatherThanThrownFromStart()
    {
        var original = new OperationCanceledException("idle raw failure");
        var dispatcher = new HeldDispatcher { CompleteFailure = original };
        var manager = new StreamManager();
        manager.Register(403, 0, dispatcher);
        var drain = manager.CompleteStreamAfterDispatchesAsync(403, 0, null).AsTask();
        Ensure(drain.IsFaulted && dispatcher.State!.IsDetached && dispatcher.Drained == 1,
            "idle callback failure returns an already physically finalized faulted operation");
        Ensure(ReferenceEquals(PhysicalTaskFailureException.GetRaw(await Failure(drain)), original),
            "idle OCE remains exact raw fault, not canceled Task conversion");
    }

    [Test]
    public void IdleSuccessfulDrainPreservesSynchronousCompletion()
    {
        var dispatcher = new HeldDispatcher();
        var manager = new StreamManager();
        manager.Register(404, 0, dispatcher);
        var drain = manager.CompleteStreamAfterDispatchesAsync(404, 0, null);
        Ensure(drain.IsCompletedSuccessfully && dispatcher.State!.IsDetached && dispatcher.Drained == 1,
            "no active dispatch and no failure require no asynchronous drain task");
        drain.GetAwaiter().GetResult();
        Ensure(dispatcher.State!.GetType().GetField("_completions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dispatcher.State) is null,
            "successful nonracing callback does not allocate the lazy completion container");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LastReleaseAndLosingDetachJoinExactlyOneHeldCallback(bool throws)
    {
        var original = new OperationCanceledException("held callback raw failure");
        using var callbackRelease = new ManualResetEventSlim();
        var dispatcher = new HeldDispatcher
        {
            CallbackRelease = callbackRelease,
            DetachFailure = throws ? original : null
        };
        var manager = new StreamManager();
        manager.Register(405, 0, dispatcher);
        var dispatch = manager.DispatchChunkAsync(405, 0, ReadOnlySequence<byte>.Empty).AsTask();
        await dispatcher.Entered.Task.WaitAsync(Timeout);
        // Exercise the actual private entry state machine, not a second lease implementation.
        Ensure((bool)Invoke(dispatcher.State!, "TryClaimJoinedDetach")!, "exact joined outcome claim");
        Invoke(dispatcher.State!, "Detach"); // Still active: last Release must be callback claimant.
        dispatcher.Release.TrySetResult();
        Task<Exception?>? callback = null;
        try
        {
            await dispatcher.CallbackEntered.Task.WaitAsync(Timeout);
            Invoke(dispatcher.State!, "Detach"); // Loser cannot run or synchronously wait on callback.
            callback = ((ValueTask<Exception?>)Invoke(dispatcher.State!, "WaitForDetachCallbackAsync")!).AsTask();
            Ensure(!callback.IsCompleted && !dispatch.IsCompleted && dispatcher.Drained == 1,
                "losing detach joins actual callback exit, not only zero dispatch count");
            Ensure(dispatcher.DetachedWasAlreadyVisible,
                "early detached signal remains available to callback without self-wait");
        }
        finally { callbackRelease.Set(); }
        await dispatch.WaitAsync(Timeout);
        var outcome = await callback!.WaitAsync(Timeout);
        Ensure(ReferenceEquals(outcome, throws ? original : null) && dispatcher.Drained == 1,
            "joined owner receives raw result after exactly one callback exits");
        manager.Unregister(405, 0);
        Ensure(dispatcher.Drained == 1, "later removal cannot repeat detached callback");
    }

    [Test]
    public async Task RunningLegacyCallbackCannotBeRetroactivelyClaimed()
    {
        var original = new InvalidOperationException("legacy callback");
        using var callbackRelease = new ManualResetEventSlim();
        var dispatcher = new HeldDispatcher { CallbackRelease = callbackRelease, DetachFailure = original };
        var manager = new StreamManager();
        manager.Register(406, 0, dispatcher);
        var detach = Task.Run(() => Invoke(dispatcher.State!, "Detach"));
        try
        {
            await dispatcher.CallbackEntered.Task.WaitAsync(Timeout);
            Ensure(!(bool)Invoke(dispatcher.State!, "TryClaimJoinedDetach")!,
                "joined ownership cannot steal an already running legacy callback");
            Invoke(dispatcher.State!, "Detach");
            Ensure(dispatcher.Drained == 1, "legacy losing detach does not duplicate callback");
        }
        finally { callbackRelease.Set(); }
        var error = await Failure(detach);
        Ensure(error is TargetInvocationException reflection && ReferenceEquals(reflection.InnerException, original),
            "legacy callback throw still propagates to its original caller");
        manager.Unregister(406, 0);
    }

    [Test]
    public void DispatcherWithoutLeaseStillPublishesCallbackExit()
    {
        var manager = new StreamManager();
        var dispatcher = new NoLeaseDispatcher();
        manager.Register(407, 0, dispatcher);
        var drain = manager.CompleteStreamAfterDispatchesAsync(407, 0, null);
        Ensure(drain.IsCompletedSuccessfully && dispatcher.Completed == 1 && manager.ActiveStreamCount == 0,
            "no-IStreamDispatchLease branch reaches successful physical completion");
        drain.GetAwaiter().GetResult();
    }

    private static object? Invoke(IStreamDispatchState state, string method)
        => state.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null);

    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private static int Count(Exception tree, Exception exact)
        => ReferenceEquals(tree, exact) ? 1 : tree is AggregateException aggregate
            ? aggregate.InnerExceptions.Sum(child => Count(child, exact)) : 0;

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HeldDispatcher : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? CompleteFailure;
        internal Exception? ClearFailure;
        internal Exception? DetachFailure;
        internal IStreamDispatchState? State;
        internal int CompleteCalls;
        internal int ClearCalls;
        internal int Drained;
        internal ManualResetEventSlim? CallbackRelease;
        internal readonly TaskCompletionSource CallbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DetachedWasAlreadyVisible;

        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAcquiredAsync(payload, 0);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => DispatchAcquiredAsync(payload, encodedByteCount);
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            Entered.TrySetResult();
            return new ValueTask(Release.Task);
        }
        public void Complete(bool isError, string? errorMessage) => Complete(null);
        public void Complete(Exception? exception)
        {
            CompleteCalls++;
            if (CompleteFailure is not null) throw CompleteFailure;
        }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId)
        {
            if (requestId != 0) return; // Registration setup is not terminal callback clearing.
            ClearCalls++;
            if (ClearFailure is not null) throw ClearFailure;
        }
        public void OnDispatchesDrained()
        {
            Interlocked.Increment(ref Drained);
            DetachedWasAlreadyVisible = State!.WaitForDetachedAsync(CancellationToken.None).IsCompletedSuccessfully;
            CallbackEntered.TrySetResult();
            if (CallbackRelease is not null && !CallbackRelease.Wait(Timeout))
                throw new TimeoutException("Test callback release barrier was not opened.");
            if (DetachFailure is not null) throw DetachFailure;
        }
    }

    private sealed class NoLeaseDispatcher : IStreamDispatcher
    {
        internal int Completed;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) => Completed++;
        public void Complete(Exception? exception) => Completed++;
    }

}
