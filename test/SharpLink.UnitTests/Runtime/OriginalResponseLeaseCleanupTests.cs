using System.Runtime.ExceptionServices;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public sealed class OriginalResponseLeaseCleanupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FirstCloseFailureStillJoinsHeldManagerDecodeAndExactLoser(bool exactWinner)
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var closeFailure = new InvalidOperationException("first original Close");
        using var release = new ManualResetEventSlim(); var codec = new HeldCodec(release);
        var sink = PooledAsyncStreamDispatcher<int>.Rent(default, codec);
        var lease = sink.CaptureOriginalLease();
        var wrapper = new CloseThrowingDispatcher(sink, closeFailure);
        var manager = new StreamManager(); manager.Register(801, 0, wrapper);
        var dispatch = Task.Run(async () => await manager.DispatchChunkAsync(801, 0, ReadOnlySequence<byte>.Empty));
        Task? winner = null; Task? loser = null; Exception? primary = null;
        try
        {
            await codec.Entered.Task.WaitAsync(Bound);
            var claimed = false;
            winner = exactWinner ? sink.DisposeOriginalLeaseAsync(lease, ref claimed).AsTask() : sink.DisposeAsync().AsTask();
            Ensure(claimed == exactWinner && !winner.IsCompleted, "winner receipt is definitive before held decode exits");
            var loserClaimed = false; loser = sink.DisposeOriginalLeaseAsync(lease, ref loserClaimed).AsTask();
            Ensure(!loserClaimed && !loser.IsCompleted && codec.Exits == 0,
                "completion-only exact loser cannot refund while first public/exact disposer joins decode");
            release.Set(); await dispatch.WaitAsync(Bound);
            var failure = await Failure(winner);
            Ensure(ReferenceEquals(exactWinner ? PhysicalTaskFailureException.GetRaw(failure) : failure, closeFailure),
                "first disposer owns the exact Close error after actual dispatch exit");
            await loser.WaitAsync(Bound);
            Ensure(codec.Exits == 1 && wrapper.State!.CloseCalls == 1, "one Close and one original decoder exit");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.Set();
            await Cleanup(primary, () => manager.Unregister(801, 0), () => sink.DisposeAsync(), dispatch, winner, loser);
            PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FirstAbandonmentPreservesRawCancellationAndLoserOwnsNoError(bool exactWinner)
    {
        var raw = new OperationCanceledException("original async abandonment cancellation");
        var release = Signal(); var entered = Signal();
        var sink = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = sink.CaptureOriginalLease(); var calls = 0;
        sink.SetConsumerAbandonedCallback(async (_, _) =>
        {
            calls++; entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(raw).Throw();
        }, 802);
        Task? winner = null; Task? loser = null; Exception? primary = null;
        try
        {
            var claimed = false;
            winner = exactWinner ? sink.DisposeOriginalLeaseAsync(lease, ref claimed).AsTask() : sink.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(Bound);
            var loserClaimed = false; loser = sink.DisposeOriginalLeaseAsync(lease, ref loserClaimed).AsTask();
            Ensure(!loserClaimed && !loser.IsCompleted, "loser joins actual callback exit, not selected Disposing state");
            release.TrySetResult(); var observed = await Failure(winner);
            Ensure(ReferenceEquals(exactWinner ? PhysicalTaskFailureException.GetRaw(observed) : observed, raw),
                "public await retains raw cancellation; exact winner wraps before cancellation conversion");
            if (exactWinner) Ensure(winner.IsFaulted, "strict exact outcome is faulted, not canceled");
            await loser.WaitAsync(Bound); Ensure(calls == 1, "completion-only join never repeats the callback");
        }
        catch (Exception error) { primary = error; throw; }
        finally { release.TrySetResult(); await Cleanup(primary, null, () => sink.DisposeAsync(), winner, loser); }
    }

    [Test]
    public async Task OldOriginalIdentityCannotDisposeReusedObject()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var first = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var oldLease = first.CaptureOriginalLease(); first.Complete(null); await first.DisposeAsync();
        var next = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        try
        {
            Ensure(ReferenceEquals(first, next) && next.CaptureOriginalLease() != oldLease, "actual same object, different generation");
            var claimed = false; await first.DisposeOriginalLeaseAsync(oldLease, ref claimed);
            Ensure(!claimed, "old identity cannot claim disposal of the new generation");
            var bytes = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, 19);
            await next.DispatchAsync(new ReadOnlySequence<byte>(bytes)); next.Complete(null);
            Ensure(await next.MoveNextAsync() && next.Current == 19, "stale cleanup leaves new codec/payload intact");
        }
        finally { await next.DisposeAsync(); PooledAsyncStreamDispatcher<int>.ClearPoolForTests(); }
    }

    [Test]
    public async Task DelayedOldWaiterCannotCompleteOrClearNewGenerationWaiter()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var firstRelease = Signal(); var oldAtInstall = Signal(); var oldRelease = Signal(); var nextRelease = Signal();
        var first = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var firstLease = first.CaptureOriginalLease();
        first.SetConsumerAbandonedCallback(async (_, _) => await firstRelease.Task.ConfigureAwait(false), 803);
        Task? firstDispose = null; Task? oldWaiter = null; Task? nextDispose = null; Task? nextWaiter = null;
        PooledAsyncStreamDispatcher<int>? next = null; Exception? primary = null;
        try
        {
            var claim = false; firstDispose = first.DisposeOriginalLeaseAsync(firstLease, ref claim).AsTask();
            first.SetBeforeConcurrentDisposeCompletionInstallForTests(() =>
            {
                oldAtInstall.TrySetResult();
                if (!oldRelease.Task.Wait(Bound)) throw new TimeoutException("old install release");
            });
            oldWaiter = Task.Run(async () => { var oldClaim = false; await first.DisposeOriginalLeaseAsync(firstLease, ref oldClaim); });
            await oldAtInstall.Task.WaitAsync(Bound); firstRelease.TrySetResult(); await firstDispose.WaitAsync(Bound);
            next = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
            Ensure(ReferenceEquals(first, next), "actual reuse before old waiter installs");
            next.SetConsumerAbandonedCallback(async (_, _) => await nextRelease.Task.ConfigureAwait(false), 804);
            var nextLease = next.CaptureOriginalLease(); var nextClaim = false;
            nextDispose = next.DisposeOriginalLeaseAsync(nextLease, ref nextClaim).AsTask();
            var nextLoser = false; nextWaiter = next.DisposeOriginalLeaseAsync(nextLease, ref nextLoser).AsTask();
            oldRelease.TrySetResult(); await oldWaiter.WaitAsync(Bound);
            Ensure(!nextWaiter.IsCompleted && !nextDispose.IsCompleted && !nextLoser,
                "resumed old waiter cannot clear or complete new generation's held cleanup");
            nextRelease.TrySetResult(); await Task.WhenAll(nextDispose, nextWaiter).WaitAsync(Bound);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            firstRelease.TrySetResult(); oldRelease.TrySetResult(); nextRelease.TrySetResult();
            await Cleanup(primary, null, () => (next ?? first).DisposeAsync(), firstDispose, oldWaiter, nextDispose, nextWaiter);
            PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task BufferCreditAndAbandonmentFailuresRemainIndependent()
    {
        var credit = new InvalidOperationException("credit"); var abandon = new OperationCanceledException("abandon");
        var sink = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = sink.CaptureOriginalLease(); var abandonCalls = 0;
        sink.SetBytesConsumedCallback((_, _, _) => throw credit, 805, 0);
        sink.SetConsumerAbandonedCallback(_ => { abandonCalls++; throw abandon; }, 805);
        Task? task = null; Exception? primary = null;
        try
        {
            await sink.DispatchAsync(new ReadOnlySequence<byte>(new byte[4]));
            var claimed = false; task = sink.DisposeOriginalLeaseAsync(lease, ref claimed).AsTask();
            var raw = PhysicalTaskFailureException.GetRaw(await Failure(task));
            Ensure(raw is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], credit) && ReferenceEquals(aggregate.InnerExceptions[1], abandon),
                "credit failure cannot skip abandonment, and neither raw error is replaced");
            var loser = false; await sink.DisposeOriginalLeaseAsync(lease, ref loser);
            Ensure(claimed && !loser && abandonCalls == 1, "independent failures still finalize exactly once");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await Cleanup(primary, null, () => sink.DisposeAsync(), task); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task LocalAbortCreditExitKeepsPublicAndExactDisposersPending(bool exactWinner, bool callbackThrows)
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        PooledAsyncStreamDispatcher<int>? previous = null;
        long previousLease = 0;
        for (var generation = 0; generation < 2; generation++)
        {
            using var release = new ManualResetEventSlim(); var entered = Signal();
            var raw = new OperationCanceledException("actual local-abort credit callback");
            var sink = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
            var lease = sink.CaptureOriginalLease(); var manager = new StreamManager();
            var local = (IStreamLocalAbortDispatcher)sink;
            Task<Exception?>? retirement = null; Task? winner = null; Task? loser = null; Exception? primary = null;
            try
            {
                if (previous is not null)
                    Ensure(ReferenceEquals(previous, sink) && lease != previousLease, "actual reuse must reset prior cleanup holder/phase");
                manager.Register(810, 0, sink);
                sink.SetBytesConsumedCallback((_, _, _) =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(Bound)) throw new TimeoutException("local-abort credit release");
                    if (callbackThrows) throw raw;
                }, 810, 0);
                await manager.DispatchChunkAsync(810, 0, new ReadOnlySequence<byte>(new byte[4]));
                local.CompleteLocalAbort(new OperationCanceledException("logical local abort"));
                retirement = Task.Run(() =>
                {
                    try { local.RetireLocalAbortBuffer(); return (Exception?)null; }
                    catch (Exception error) { return error; }
                });
                await entered.Task.WaitAsync(Bound);
                var claimed = false;
                winner = exactWinner ? sink.DisposeOriginalLeaseAsync(lease, ref claimed).AsTask() : sink.DisposeAsync().AsTask();
                var loserClaimed = false; loser = sink.DisposeOriginalLeaseAsync(lease, ref loserClaimed).AsTask();
                Ensure(claimed == exactWinner && !loserClaimed && !winner.IsCompleted && !loser.IsCompleted,
                    "zero manager dispatch count and claimed local-abort phase are not credit-callback exit");
                release.Set(); var observed = await retirement.WaitAsync(Bound);
                Ensure(callbackThrows ? ReferenceEquals(observed, raw) : observed is null,
                    "actual buffer retirement caller exclusively owns its raw callback outcome");
                await Task.WhenAll(winner, loser).WaitAsync(Bound);
                previous = sink; previousLease = lease;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                release.Set();
                await Cleanup(primary, () => manager.Unregister(810, 0), () => sink.DisposeAsync(), retirement, winner, loser);
            }
        }
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalAbortExitAndDisposeSerializeOnExistingGate()
    {
        using var release = new ManualResetEventSlim(); var entered = Signal(); var callbackReturning = Signal();
        var sink = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = sink.CaptureOriginalLease(); var local = (IStreamLocalAbortDispatcher)sink;
        var manager = new StreamManager(); manager.Register(814, 0, sink);
        var gate = (Lock)typeof(PooledAsyncStreamDispatcher<int>).GetField("_dispatchStateGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sink)!;
        Task? retirement = null; Task? disposal = null; Exception? primary = null;
        sink.SetBytesConsumedCallback((_, _, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("exit gate credit release");
            callbackReturning.TrySetResult();
        }, 814, 0);
        try
        {
            await manager.DispatchChunkAsync(814, 0, new ReadOnlySequence<byte>(new byte[4]));
            local.CompleteLocalAbort(null); retirement = Task.Run(local.RetireLocalAbortBuffer);
            await entered.Task.WaitAsync(Bound);
            lock (gate)
            {
                release.Set();
                Ensure(callbackReturning.Task.Wait(Bound), "real callback reached exit publication gate");
                disposal = Task.Run(async () => { var claimed = false; await sink.DisposeOriginalLeaseAsync(lease, ref claimed); });
                Ensure(SpinWait.SpinUntil(() =>
                {
                    try { _ = sink.CaptureOriginalLease(); return false; }
                    catch (InvalidOperationException) { return true; }
                }, Bound), "real disposer claimed original generation before reaching held gate");
                Ensure(!retirement.IsCompleted && !disposal.IsCompleted,
                    "callback exit and disposer cannot bypass the actual held publication gate");
            }
            await Task.WhenAll(retirement, disposal).WaitAsync(Bound);
        }
        catch (Exception error) { primary = error; throw; }
        finally { release.Set(); await Cleanup(primary, () => manager.Unregister(814, 0), () => sink.DisposeAsync(), retirement, disposal); }
    }

    [Test]
    public async Task LocalAbortCallbackSelfDisposeRejectsBeforeClaim()
    {
        var sink = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = sink.CaptureOriginalLease(); var local = (IStreamLocalAbortDispatcher)sink;
        var attempts = 0; var claimed = false;
        sink.SetBytesConsumedCallback((_, _, _) =>
        {
            try { _ = sink.DisposeAsync(); throw new InvalidOperationException("missing public self-wait rejection"); }
            catch (InvalidOperationException error) { Ensure(error.Message.Contains("cannot wait for its own", StringComparison.Ordinal), "public self-wait gate"); attempts++; }
            try { _ = sink.DisposeOriginalLeaseAsync(lease, ref claimed); throw new InvalidOperationException("missing exact self-wait rejection"); }
            catch (InvalidOperationException error) { Ensure(error.Message.Contains("cannot wait for its own", StringComparison.Ordinal), "exact self-wait gate"); attempts++; }
        }, 811, 0);
        try
        {
            await sink.DispatchAsync(new ReadOnlySequence<byte>(new byte[4]));
            local.CompleteLocalAbort(null); local.RetireLocalAbortBuffer();
            Ensure(attempts == 2 && !claimed && sink.CaptureOriginalLease() == lease,
                "same-thread callback reentry rejects before moving disposal ownership");
        }
        finally { local.RetireLocalAbortBuffer(); await sink.DisposeAsync(); }
    }

    [Test]
    public async Task NestedLocalAbortCallbackRejectsAncestorDisposalWithoutAmbientStack()
    {
        var first = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var second = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var firstLocal = (IStreamLocalAbortDispatcher)first; var secondLocal = (IStreamLocalAbortDispatcher)second;
        var lease = first.CaptureOriginalLease(); var rejected = false;
        first.SetBytesConsumedCallback((_, _, _) => secondLocal.RetireLocalAbortBuffer(), 812, 0);
        second.SetBytesConsumedCallback((_, _, _) =>
        {
            var claimed = false;
            try { _ = first.DisposeOriginalLeaseAsync(lease, ref claimed); throw new InvalidOperationException("missing ancestor self-wait rejection"); }
            catch (InvalidOperationException error)
            { Ensure(error.Message.Contains("cannot wait for its own", StringComparison.Ordinal) && !claimed, "A to B to Dispose(A) exact self-wait gate"); rejected = true; }
        }, 813, 0);
        try
        {
            await first.DispatchAsync(new ReadOnlySequence<byte>(new byte[4]));
            await second.DispatchAsync(new ReadOnlySequence<byte>(new byte[4]));
            firstLocal.CompleteLocalAbort(null); secondLocal.CompleteLocalAbort(null);
            firstLocal.RetireLocalAbortBuffer();
            Ensure(rejected && first.CaptureOriginalLease() == lease, "nested synchronous callback leaves actual owner intact");
        }
        finally
        {
            Exception? failure = null;
            try { firstLocal.RetireLocalAbortBuffer(); } catch (Exception error) { failure = error; }
            try { secondLocal.RetireLocalAbortBuffer(); } catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
            try { await first.DisposeAsync().AsTask().WaitAsync(Bound); }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
            try { await second.DisposeAsync().AsTask().WaitAsync(Bound); }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class HeldCodec(ManualResetEventSlim release) : IRpcCodec<int>
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal int Exits;
        public void Serialize(in int value, IBufferWriter<byte> writer) => Int32Codec.Instance.Serialize(value, writer);
        public int Deserialize(in ReadOnlySequence<byte> payload)
        {
            Entered.TrySetResult();
            try { if (!release.Wait(Bound)) throw new TimeoutException("held decode release"); return 1; }
            finally { Interlocked.Increment(ref Exits); }
        }
    }
    private sealed class CloseThrowingDispatcher(PooledAsyncStreamDispatcher<int> sink, Exception failure) : IStreamDispatcher, IStreamDispatchLease
    {
        internal CloseThrowingState? State;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => sink.DispatchAsync(payload);
        public void Complete(bool isError, string? message) => sink.Complete(isError, message);
        public void Complete(Exception? error) => sink.Complete(error);
        public void BindDispatchState(IStreamDispatchState state)
        {
            State = new CloseThrowingState(state, failure);
            ((IStreamDispatchLease)sink).BindDispatchState(State);
        }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int bytes) => ((IStreamDispatchLease)sink).DispatchAcquiredAsync(payload, bytes);
        public void OnDispatchesDrained() => ((IStreamDispatchLease)sink).OnDispatchesDrained();
    }
    private sealed class CloseThrowingState(IStreamDispatchState original, Exception failure) : IStreamDispatchState
    {
        internal int CloseCalls;
        public bool HasActiveDispatches => original.HasActiveDispatches;
        public bool IsDetached => original.IsDetached;
        public ValueTask WaitForDispatchesDrainedAsync() => original.WaitForDispatchesDrainedAsync();
        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken) => original.WaitForDetachedAsync(cancellationToken);
        public void Close() { CloseCalls++; original.Close(); throw failure; }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected an actual failure.");
    }
    private static async Task Cleanup(Exception? primary, Action? release, Func<ValueTask>? dispose, params Task?[] tasks)
    {
        Exception? failure = primary;
        foreach (var task in tasks)
        {
            if (task is null) continue;
            await task.WaitAsync(Bound).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!task.IsCompleted) failure = RpcPublicationErrors.Combine(failure, new TimeoutException("cleanup task did not physically exit"));
        }
        try { release?.Invoke(); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        try { if (dispose is not null) await dispose().AsTask().WaitAsync(Bound); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (failure is not null && !ReferenceEquals(failure, primary)) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
