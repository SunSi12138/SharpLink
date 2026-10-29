using System.Buffers;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public class PooledAsyncStreamDispatcherLocalAbortTests
{
    [Test]
    public async Task LocalAbortShouldPreemptBufferedDeliveryAndReturnCredit()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 71,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(1));
        await dispatcher.DispatchAsync(Encode(2));
        await dispatcher.DispatchAsync(Encode(3));

        var terminal = new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "test deadline");
        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        localAbort.CompleteLocalAbort(terminal);

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a local deadline terminal must win before any buffered response item is delivered");
        Ensure(Volatile.Read(ref creditedBytes) == 0,
            "buffered receive credit must remain owned until local-abort retirement");

        localAbort.RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref creditedBytes) == 12,
            "retiring three discarded Int32 frames must return all buffered receive credit");

        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task ConsumerDeliveryGateShouldClaimDeadlineBeforeBufferedItemPublishes()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 74,
            streamId: 0);
        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        var terminal = new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "deadline claimed at delivery");
        var gate = new DeadlineDeliveryGate(localAbort, terminal);
        dispatcher.SetConsumerAbandonedCallback(gate.OnConsumerAbandonedAsync, requestId: 74);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(17));

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(gate.ClaimCount == 1,
            "the owning logical call must be consulted before the buffered dequeue claim");
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a delivery-time deadline win must preempt a previously buffered response item");
        Ensure(Volatile.Read(ref creditedBytes) == 0,
            "the preempted buffered item must not publish receive credit as delivered");

        localAbort.RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "retiring the preempted buffered item must return its receive credit exactly once");
        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task DeliveryGateMustNotPreemptBufferedItemsAfterRemoteTerminalWon()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var gate = new AlreadyTerminalDeliveryGate();
        dispatcher.SetConsumerAbandonedCallback(gate.OnConsumerAbandonedAsync, requestId: 75);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(23));
        dispatcher.Complete(new SharpLinkException(
            SharpLinkErrorCode.RemoteError,
            "peer terminal"));

        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 23,
            "a peer terminal that already owns the call must retain normal buffered-drain semantics");
        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.RemoteError },
            "the peer terminal must surface after its preceding buffered item drains");
        Ensure(gate.ClaimCount >= 1,
            "the dispatcher should still consult the owning gate before user-visible delivery");

        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalAbortShouldWaitForOwnedBufferedPublication()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        var deliveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDelivery = new ManualResetEventSlim();
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) =>
            {
                deliveryEntered.TrySetResult();
                if (!releaseDelivery.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("buffered delivery publication was not released");
                Interlocked.Add(ref creditedBytes, bytes);
            },
            requestId: 73,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();
        await dispatcher.DispatchAsync(Encode(41));

        await LongRunningTestWorker.Run(() =>
        {
            Task<bool>? delivery = null;
            Task? abort = null;
            try
            {
                delivery = LongRunningTestWorker.RunAsync(() => enumerator.MoveNextAsync().AsTask());
                LongRunningTestWorker.WaitForPhase(deliveryEntered.Task, TimeSpan.FromSeconds(5));

                var terminal = new SharpLinkException(
                    SharpLinkErrorCode.DeadlineExceeded,
                    "test deadline");
                var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
                var abortEnteringDispatcher = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                dispatcher.SetBeforeProducerOperationAcquireForTests(
                    () => abortEnteringDispatcher.TrySetResult());
                abort = LongRunningTestWorker.Run(() => localAbort.CompleteLocalAbort(terminal));

                // Wait for CompleteLocalAbort to enter the dispatch-acquire path while the delivery
                // callback still owns publication. This replaces a wall-clock sleep that only guessed
                // that the competing worker had been scheduled.
                LongRunningTestWorker.WaitForPhase(abortEnteringDispatcher.Task, TimeSpan.FromSeconds(5));
                var abortWaitedForPublication = !abort.IsCompleted;
                dispatcher.SetBeforeProducerOperationAcquireForTests(null);

                releaseDelivery.Set();
                var delivered = LongRunningTestWorker.WaitForPhase(delivery, TimeSpan.FromSeconds(5));
                LongRunningTestWorker.WaitForPhase(abort, TimeSpan.FromSeconds(5));

                Ensure(abortWaitedForPublication,
                    "a local terminal must not complete while an owned item is still publishing Current or receive credit");
                Ensure(delivered && enumerator.Current == 41,
                    "the item that acquired publication ownership first must finish before the local terminal wins");
                Ensure(Volatile.Read(ref creditedBytes) == 4,
                    "the winning delivery must publish its receive credit exactly once");

                var failure = LongRunningTestWorker.WaitForPhase(CaptureFailureAsync(enumerator.MoveNextAsync().AsTask()), TimeSpan.FromSeconds(5));
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                    "the local terminal must close every delivery after the already-owned item");

                localAbort.RetireLocalAbortBuffer();
                Ensure(Volatile.Read(ref creditedBytes) == 4,
                    "retirement must not return credit twice for an item that already published");
            }
            finally
            {
                // Neither the publication owner nor its controller depends on the shared pool.
                releaseDelivery.Set();
                dispatcher.SetBeforeProducerOperationAcquireForTests(null);
                if (delivery is not null) LongRunningTestWorker.Join(delivery, TimeSpan.FromSeconds(5));
                if (abort is not null) LongRunningTestWorker.Join(abort, TimeSpan.FromSeconds(5));
                LongRunningTestWorker.WaitForPhase(enumerator.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
                PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
            }
        });
    }

    [Test]
    public async Task LocalAbortCleanupRetentionShouldOutliveEarlyConsumerDispose()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        // Observe this lease, not the process-wide int pool that unrelated tests also use.
        var returnTransitions = 0;
        dispatcher.SetAfterReturnTransitionForTests(() => Interlocked.Increment(ref returnTransitions));
        var unrelated = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 72,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();
        await dispatcher.DispatchAsync(Encode(7));

        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        localAbort.CompleteLocalAbort(new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "test deadline"));

        // Consumer disposal may race the connection's dispatch-drain continuation. It may
        // perform the one-shot buffer drain, but it must not return the dispatcher to the
        // process-wide pool before the terminal owner releases its cleanup retention.
        await enumerator.DisposeAsync();

        // A different dispatcher may return between disposal and the retention assertion.
        // Force that interleaving without depending on parallel scheduling or wall-clock waits.
        unrelated.Complete(exception: null);
        await unrelated.DisposeAsync();
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "early disposal must preserve receive credit while sharing local-abort retirement");
        Ensure(Volatile.Read(ref returnTransitions) == 0,
            "the terminal cleanup retention must keep a disposed dispatcher out of the pool");

        localAbort.RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref returnTransitions) == 1,
            "releasing terminal cleanup ownership must return the finalized dispatcher exactly once");
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "terminal cleanup must not return credit twice after early disposal");
    }

    [Test]
    public async Task RemoteTerminalShouldStillDrainBufferedItemsBeforeError()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(11));
        await dispatcher.DispatchAsync(Encode(22));
        dispatcher.Complete(new SharpLinkException(
            SharpLinkErrorCode.RemoteError,
            "peer terminal"));

        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 11,
            "a genuine peer terminal must preserve delivery of the first preceding buffered item");
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 22,
            "a genuine peer terminal must preserve delivery of all preceding buffered items");

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.RemoteError },
            "the peer terminal error must surface only after preceding buffered items drain");

        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    private static ReadOnlySequence<byte> Encode(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        Int32Codec.Instance.Serialize(in value, writer);
        return new ReadOnlySequence<byte>(writer.WrittenMemory);
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Exception("expected failure");
    }

    private sealed class DeadlineDeliveryGate(
        IStreamLocalAbortDispatcher localAbort,
        Exception terminal) : IStreamConsumerDeliveryGate
    {
        private int _claimCount;

        internal int ClaimCount => Volatile.Read(ref _claimCount);

        public bool TryAcceptStreamDelivery(long requestId)
        {
            Interlocked.Increment(ref _claimCount);
            localAbort.CompleteLocalAbort(terminal);
            return false;
        }

        internal ValueTask OnConsumerAbandonedAsync(
            long requestId,
            IStreamDispatchState? dispatchState)
            => ValueTask.CompletedTask;
    }

    private sealed class AlreadyTerminalDeliveryGate : IStreamConsumerDeliveryGate
    {
        private int _claimCount;

        internal int ClaimCount => Volatile.Read(ref _claimCount);

        public bool TryAcceptStreamDelivery(long requestId)
        {
            Interlocked.Increment(ref _claimCount);
            return false;
        }

        internal ValueTask OnConsumerAbandonedAsync(
            long requestId,
            IStreamDispatchState? dispatchState)
            => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
