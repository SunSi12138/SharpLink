using System.Threading;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SharpLink.UnitTests.Runtime;

public class PooledAsyncStreamDispatcherTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);

    private static readonly ReadOnlySequence<byte> Payload = new(new byte[] { 1 });
    private static readonly ReadOnlySequence<byte> NullStringPayload = new(new byte[] { 255, 255, 255, 255 });

    [Test]
    [NotInParallel]
    public async Task RequiredClientResponseStreamMustRejectDecodedNull()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new NullReferenceItemCodec());

        var failure = await CaptureDispatchFailureAsync(() => dispatcher.DispatchAsync(Payload));
        dispatcher.Complete(failure);
        await dispatcher.DisposeAsync();

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "a required Client response stream item decoded as null must be DataLoss");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();

        var nullableDispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new NullReferenceItemCodec(),
            payloadNullable: true);
        await nullableDispatcher.DispatchAsync(Payload);
        nullableDispatcher.Complete(exception: null);
        var enumerator = nullableDispatcher.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current is null,
            "an explicitly nullable Client response stream item must preserve null");
        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task RequiredServerRequestStreamMustRejectDecodedNull()
    {
        PooledAsyncStreamDispatcher<string>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string>.Rent(
            default,
            RpcSessionTestFixture.RuntimeContext.Codecs);

        var failure = await CaptureDispatchFailureAsync(() => dispatcher.DispatchAsync(NullStringPayload));
        dispatcher.Complete(failure);
        await dispatcher.DisposeAsync();

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "a required Server request stream item decoded as null must be DataLoss");
        PooledAsyncStreamDispatcher<string>.ClearPoolForTests();

        var nullableDispatcher = PooledAsyncStreamDispatcher<string>.Rent(
            default,
            RpcSessionTestFixture.RuntimeContext.Codecs,
            payloadNullable: true);
        await nullableDispatcher.DispatchAsync(NullStringPayload);
        nullableDispatcher.Complete(exception: null);
        var enumerator = nullableDispatcher.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current is null,
            "an explicitly nullable Server request stream item must preserve null");
        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<string>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task ConsumerCancellationTokenShouldNotMaskLeaseCancellation()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        using var leaseCancellation = new CancellationTokenSource();
        using var consumerCancellation = new CancellationTokenSource();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            leaseCancellation.Token,
            new ReferenceItemCodec());
        var enumerator = dispatcher.GetAsyncEnumerator(consumerCancellation.Token);
        var waiting = enumerator.MoveNextAsync().AsTask();

        leaseCancellation.Cancel();
        var completed = await Task.WhenAny(
            waiting,
            Task.Delay(TimeSpan.FromMilliseconds(250)));

        consumerCancellation.Cancel();
        _ = await CaptureFailureAsync(waiting);
        await enumerator.DisposeAsync();
        Ensure(ReferenceEquals(completed, waiting),
            "the call/lease cancellation token must remain effective when the consumer supplies another token");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public void PoolShouldRetainAtMost1024DispatchersAfterBurst()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var discarded = FillPoolAndReturnDiscardedReference();

        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1024,
            "pool retention must be bounded after a 10,000-stream burst");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Ensure(!discarded.IsAlive, "dispatchers above the retention cap must remain collectible");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FillPoolAndReturnDiscardedReference()
    {
        var codec = new ReferenceItemCodec();
        var dispatchers = new PooledAsyncStreamDispatcher<ReferenceItem>[10_000];
        for (var index = 0; index < dispatchers.Length; index++)
            dispatchers[index] = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);

        for (var index = 0; index < dispatchers.Length; index++)
        {
            dispatchers[index].Complete(exception: null);
            dispatchers[index].DisposeAsync().GetAwaiter().GetResult();
        }

        var discarded = new WeakReference(dispatchers[^1]);
        Array.Clear(dispatchers);
        return discarded;
    }

    [Test]
    [NotInParallel]
    public async Task LargeBufferShouldShrinkAndClearReferencesBeforePooling()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        for (var index = 0; index < 300; index++)
            await dispatcher.DispatchAsync(Payload);

        Ensure(dispatcher.BufferCapacityForTests > 256, "test must grow the receive buffer");
        dispatcher.Complete(exception: null);
        await dispatcher.DisposeAsync();

        Ensure(dispatcher.BufferCapacityForTests == 16,
            "buffers larger than 256 elements must shrink before pool retention");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "pooled dispatcher must not retain decoded items or lease callbacks");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task PoolReturnShouldClearCodecCallbacksAndCancellationRegistration()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        using var cancellation = new CancellationTokenSource();
        var marker = new object();
        var codec = new ReferenceItemCodec(marker);
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(cancellation.Token, codec);
        dispatcher.SetBytesConsumedCallback((_, _, _) => GC.KeepAlive(marker), 1, 2);
        dispatcher.SetConsumerAbandonedCallback(_ => GC.KeepAlive(marker), 1);
        _ = dispatcher.GetAsyncEnumerator();
        await dispatcher.DispatchAsync(Payload);

        dispatcher.Complete(exception: null);
        await dispatcher.DisposeAsync();

        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "codec, callbacks, cancellation registration and decoded items must be cleared");
        cancellation.Cancel();
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task PoolReturnShouldClearActiveSegmentWhenFreeSegmentStackIsEmpty()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Payload);
        Ensure(await enumerator.MoveNextAsync(), "the active-segment item must be consumable");
        Ensure(IsFreeSegmentStackEmpty(dispatcher),
            "a one-item stream must not create a recycled free segment");

        // TryDequeue clears consumed slots, so seed stale state to exercise the pool cleanup itself.
        var activeSegment = GetPrivateField(dispatcher, "_consumerSegment");
        SeedStaleSegmentState(activeSegment);

        dispatcher.Complete(exception: null);
        await enumerator.DisposeAsync();

        EnsureSegmentStateWasCleared(activeSegment, "the active segment");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "pooling with an empty free-segment stack must not retain a stale item");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task PoolReturnShouldClearAndRecycleFreeSegmentsWhenStackIsNonempty()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var enumerator = dispatcher.GetAsyncEnumerator();
        for (var index = 0; index < 17; index++)
            await dispatcher.DispatchAsync(Payload);
        for (var index = 0; index < 17; index++)
            Ensure(await enumerator.MoveNextAsync(), $"item {index} must be consumable");

        var recycledSegment = GetOnlyFreeSegment(dispatcher);
        // TryDequeue clears consumed slots, so seed stale state to exercise the pool cleanup itself.
        SeedStaleSegmentState(recycledSegment);

        dispatcher.Complete(exception: null);
        await enumerator.DisposeAsync();

        EnsureSegmentStateWasCleared(recycledSegment, "the recycled free segment");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "pooling with recycled free segments must not retain a stale item");

        var reused = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        Ensure(ReferenceEquals(dispatcher, reused),
            "the test must rent the dispatcher that owns the cleared recycled segment");
        var reusedEnumerator = reused.GetAsyncEnumerator();
        for (var index = 0; index < 33; index++)
            await reused.DispatchAsync(Payload);
        reused.Complete(exception: null);

        var received = 0;
        while (await reusedEnumerator.MoveNextAsync())
            received++;
        await reusedEnumerator.DisposeAsync();

        Ensure(received == 33,
            "the next lease must safely reuse every cleared free segment");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task CompletedEnumeratorShouldNotReturnBeforeCallerDisposesIt()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var enumerator = dispatcher.GetAsyncEnumerator();
        dispatcher.Complete(exception: null);

        Ensure(!await enumerator.MoveNextAsync(), "completed stream should end enumeration");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "MoveNext(false) must not pool an enumerator that await foreach still has to dispose");

        await enumerator.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "explicit enumerator disposal should return the completed dispatcher");
        await enumerator.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "repeated disposal must not return a dispatcher twice");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "an idempotent disposal after pool return must not retain a completion holder on the common path");

        var reused = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        Ensure(ReferenceEquals(dispatcher, reused),
            "a repeated disposal must not install stale completion state on the returned dispatcher");
        reused.Complete(exception: null);
        await reused.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the next lease must still complete and return after an idempotent old disposal");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public void SynchronousNoWaiterDisposeShouldNotAllocateCompletionState()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var codec = new ReferenceItemCodec();

        // Warm the exact terminal/disposal path before measuring it. The state intentionally
        // remains attached during the measurement so ConcurrentStack pool-node allocation is
        // excluded; this measures only the first no-waiter DisposeAsync coordination.
        CompleteAndDisposeWhileAttached(codec);

        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
        var lease = (IStreamDispatchLease)dispatcher;
        var dispatchState = new AttachedDispatchState();
        lease.BindDispatchState(dispatchState);
        dispatcher.Complete(exception: null);

        var before = GC.GetAllocatedBytesForCurrentThread();
        dispatcher.DisposeAsync().GetAwaiter().GetResult();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(allocated == 0,
            $"a synchronous no-waiter disposal allocated {allocated} bytes for completion coordination");
        Ensure(dispatchState.CloseCount == 2,
            "remote completion and consumer disposal must each close the attached dispatch state");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "the attached terminal state must prevent pool return until it is detached");

        dispatchState.Detach();
        lease.OnDispatchesDrained();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the finalized no-waiter dispatcher must return once detach completes");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "pool return after a no-waiter disposal must not retain a completion holder");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task DelayedOldPoolReturnShouldNotReturnOrClearReusedLease()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        using var dispatchState = new CoordinatedPoolReturnState();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var oldLease = (IStreamDispatchLease)dispatcher;
        oldLease.BindDispatchState(dispatchState);
        dispatcher.Complete(exception: null);
        await dispatcher.DisposeAsync();

        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "the attached old lease must remain outside the pool before the coordinated returns");

        dispatchState.CoordinateReturns();
        var firstReturn = Task.Run(() => oldLease.OnDispatchesDrained());
        var secondReturn = Task.Run(() => oldLease.OnDispatchesDrained());
        Ensure(dispatchState.WaitForBothPrechecks(TimeSpan.FromSeconds(3)),
            "both old-lease return contenders must reach the final eligibility precheck");

        var winner = await Task.WhenAny(firstReturn, secondReturn).WaitAsync(TimeSpan.FromSeconds(3));
        await winner;
        var delayedReturn = ReferenceEquals(winner, firstReturn) ? secondReturn : firstReturn;
        Ensure(!delayedReturn.IsCompleted,
            "one old-lease contender must remain delayed while the other returns the dispatcher");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the winning old-lease contender must return the dispatcher exactly once");

        var reused = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec(new object()));
        Ensure(ReferenceEquals(dispatcher, reused),
            "the test must rent the exact dispatcher instance returned by the winning contender");

        dispatchState.ReleaseDelayedReturn();
        await Task.WhenAll(firstReturn, secondReturn).WaitAsync(TimeSpan.FromSeconds(3));

        var retainedAfterDelayedReturn =
            PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests;
        var reusedLeaseReferencesIntact = reused.HasRetainedReferencesForTests;
        if (retainedAfterDelayedReturn == 0)
        {
            reused.Complete(exception: null);
            await reused.DisposeAsync();
        }
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();

        Ensure(retainedAfterDelayedReturn == 0 && reusedLeaseReferencesIntact,
            $"a delayed old return must neither pool nor clear the reused lease " +
            $"(retained={retainedAfterDelayedReturn}, referencesIntact={reusedLeaseReferencesIntact})");
    }

    [Test]
    [NotInParallel]
    public async Task EarlyDisposeShouldNotPoolWhileProducerIsDecoding()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockingCodec = new ReferenceItemCodec(
            marker: null,
            beforeDeserialize: () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        var first = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, blockingCodec);
        var manager = new StreamManager();
        manager.Register(77, first);
        first.SetConsumerAbandonedCallback(_ => manager.Unregister(77), 77);

        var producer = Task.Run(async () => await manager.DispatchChunkAsync(77, Payload));
        Ensure(entered.Wait(TimeSpan.FromSeconds(3)), "producer must enter decode");
        var disposing = first.DisposeAsync().AsTask();
        Ensure(!disposing.IsCompleted, "early disposal must wait for an acquired decode");

        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(3));

        var second = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        await producer.WaitAsync(TimeSpan.FromSeconds(3));
        second.Complete(exception: null);
        await second.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the safely reused dispatcher should return exactly once");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task ConsumerDisposeShouldAwaitDispatchDrainBeforeFinalCreditAndAbandonmentCallback()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        using var dispatchEntered = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        var events = new List<string>();
        var finalCredit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonmentCallback = new TaskCompletionSource<IStreamDispatchState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonmentCallbackCount = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            null,
            (_, _, _) =>
            {
                events.Add("final-credit");
                finalCredit.TrySetResult();
            },
            null);
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec(
                marker: null,
                beforeDeserialize: () =>
                {
                    events.Add("dispatch-entered");
                    dispatchEntered.Set();
                    releaseDispatch.Wait(RaceCoordinationTimeout);
                }));
        const long requestId = 93;
        manager.Register(requestId, dispatcher);
        dispatcher.SetConsumerAbandonedCallback(
            (abandonedRequestId, dispatchState) =>
            {
                var drainedDispatchState = dispatchState ?? throw new Exception(
                    "the registered dispatcher must expose its dispatch state to abandonment cleanup");
                Ensure(!drainedDispatchState.HasActiveDispatches,
                    "the abandonment callback must observe the acquired dispatch as drained");
                Interlocked.Increment(ref abandonmentCallbackCount);
                events.Add("consumer-abandoned");
                abandonmentCallback.TrySetResult(drainedDispatchState);
                manager.Unregister(abandonedRequestId);
                return ValueTask.CompletedTask;
            },
            requestId);

        var producer = Task.Run(async () => await manager.DispatchChunkAsync(requestId, Payload));
        Ensure(dispatchEntered.Wait(RaceCoordinationTimeout),
            "the producer must hold the stream-manager dispatch lease before disposal starts");
        var disposing = dispatcher.DisposeAsync().AsTask();
        var concurrentDispose = dispatcher.DisposeAsync().AsTask();
        Ensure(!disposing.IsCompleted && !concurrentDispose.IsCompleted &&
               !finalCredit.Task.IsCompleted && !abandonmentCallback.Task.IsCompleted,
            "consumer disposal callers must await the dispatch-drained signal before final credit or abandonment callback");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "the dispatcher must not return to the pool while its acquired dispatch is still active");

        releaseDispatch.Set();
        await Task.WhenAll(
            producer,
            finalCredit.Task,
            abandonmentCallback.Task,
            disposing,
            concurrentDispose).WaitAsync(RaceCoordinationTimeout);

        Ensure(events.Count == 3 &&
               events[0] == "dispatch-entered" &&
               events[1] == "final-credit" &&
               events[2] == "consumer-abandoned",
            "the acquired dispatch must publish final credit before consumer-abandoned cleanup");
        Ensure(Volatile.Read(ref abandonmentCallbackCount) == 1,
            "consumer abandonment must invoke its terminal callback exactly once across concurrent disposal callers");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the dispatcher must finalize and return only after the drain and abandonment callback complete");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCompletionBeforeConsumerDisposeShouldSkipAbandonmentAndCompleteSynchronously()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var abandonmentCallbacks = 0;
        dispatcher.SetConsumerAbandonedCallback(
            _ => Interlocked.Increment(ref abandonmentCallbacks),
            requestId: 94);

        dispatcher.Complete(exception: null);
        var disposing = dispatcher.DisposeAsync();
        Ensure(disposing.IsCompletedSuccessfully,
            "the normal no-waiter remote-completion path must not suspend consumer disposal");
        await disposing;

        Ensure(Volatile.Read(ref abandonmentCallbacks) == 0,
            "a remote terminal completion must not be reported as consumer abandonment");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the normal remote-completion path must retain the completed dispatcher once");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task RemoteCompletionMustHoldConsumerDisposeUntilTerminalPublicationFinishes()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var state = new GatedCloseDispatchState();
        ((IStreamDispatchLease)dispatcher).BindDispatchState(state);
        var abandonmentCallbacks = 0;
        dispatcher.SetConsumerAbandonedCallback(
            _ => Interlocked.Increment(ref abandonmentCallbacks),
            requestId: 95);

        var remoteComplete = Task.Run(() => dispatcher.Complete(exception: null));
        await state.FirstCloseEntered.WaitAsync(RaceCoordinationTimeout);
        var consumerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = Task.Run(async () =>
        {
            consumerStarted.TrySetResult();
            await dispatcher.DisposeAsync();
        });
        try
        {
            await consumerStarted.Task.WaitAsync(RaceCoordinationTimeout);
            Ensure(!disposing.IsCompleted,
                "consumer disposal must not bypass a remote terminal publication that is still closing its dispatch state");
            Ensure(!state.WasClosedConcurrently,
                "consumer disposal must not race a remote terminal close before that terminal publication finishes");
            Ensure(state.CloseCount == 1,
                "the remote terminal must own exactly the first dispatch-state close before consumer disposal resumes");

            state.ReleaseFirstClose();
            await Task.WhenAll(remoteComplete, disposing).WaitAsync(RaceCoordinationTimeout);

            Ensure(Volatile.Read(ref abandonmentCallbacks) == 0,
                "a remote terminal winner must not report consumer abandonment while disposal joins it");
            Ensure(state.CloseCount == 2 && !state.WasClosedConcurrently,
                "the consumer must perform its second dispatch-state close only after remote publication finishes");
            Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
                "pool return must wait for the remote terminal publication and consumer disposal to finish");
        }
        finally
        {
            state.ReleaseFirstClose();
            await Task.WhenAll(remoteComplete, disposing).WaitAsync(RaceCoordinationTimeout);
            PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        }
    }

    [Test]
    [NotInParallel]
    public async Task LateDispatchStateBindingAfterTerminalCompletionMustCloseTheState()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var state = new GatedCloseDispatchState(blockFirstClose: false);

        dispatcher.Complete(exception: null);
        ((IStreamDispatchLease)dispatcher).BindDispatchState(state);
        Ensure(state.IsClosed,
            "a dispatch state bound after terminal completion must be closed before it can accept another frame");

        await dispatcher.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the late-bound closed state must still allow the completed dispatcher to return once");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task AttachedDispatcherShouldNotReturnToPoolWhenPendingCompletionOwnsTheSlot()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var manager = new StreamManager();
        var first = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        manager.Register(91, first);
        first.SetConsumerAbandonedCallback(_ => { }, 91);

        await first.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "a closed but still attached dispatcher must not enter the process-wide pool");

        var second = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        Ensure(!ReferenceEquals(first, second),
            "a delayed pending completion must not share its dispatcher with a new lease");

        manager.Unregister(91);
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "detaching the old entry should make its disposed dispatcher reusable");
        second.Complete(exception: null);
        await second.DisposeAsync();
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task AsyncConsumerAbandonmentShouldJoinTerminalCleanupBeforeDisposeReturns()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var manager = new StreamManager();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        manager.Register(92, dispatcher);
        var callbackEntered = new TaskCompletionSource<IStreamDispatchState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.SetConsumerAbandonedCallback(
            async (requestId, dispatchState) =>
            {
                callbackEntered.TrySetResult(dispatchState ?? throw new Exception(
                    "registered dispatcher must expose its dispatch state"));
                await releaseCallback.Task.ConfigureAwait(false);
                manager.Unregister(requestId);
            },
            92);

        var disposing = dispatcher.DisposeAsync().AsTask();
        var state = await callbackEntered.Task.WaitAsync(RaceCoordinationTimeout);
        var deferredContinuations = new QueuedSynchronizationContext();
        var originalContext = SynchronizationContext.Current;
        Task concurrentDispose;
        try
        {
            SynchronizationContext.SetSynchronizationContext(deferredContinuations);
            concurrentDispose = dispatcher.DisposeAsync().AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        Ensure(!disposing.IsCompleted && !concurrentDispose.IsCompleted && !state.IsDetached,
            "disposal must join asynchronous terminal cleanup before finalizing its lease");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "an asynchronously cleaning dispatcher must not enter the pool");

        releaseCallback.TrySetResult();
        await disposing.WaitAsync(RaceCoordinationTimeout);
        var reused = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        Ensure(ReferenceEquals(dispatcher, reused),
            "the first disposal must return the exact old lease before the concurrent caller resumes");
        await concurrentDispose.WaitAsync(RaceCoordinationTimeout);
        Ensure(deferredContinuations.PostCount == 0,
            "a concurrent disposal must await its generation-scoped completion instead of polling through a queued continuation");
        Ensure(state.IsDetached,
            "the terminal callback must detach the completed stream before disposal returns");
        reused.Complete(exception: null);
        await reused.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the dispatcher should become reusable only after terminal cleanup completes");
        Ensure(!dispatcher.HasRetainedReferencesForTests,
            "the old generation's concurrent-dispose completion must not remain on the reused pooled dispatcher");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task StaleConcurrentDisposeCompletionMustNotPoisonTheNextLease()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var firstCleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWaiterAtInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNextCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        first.SetConsumerAbandonedCallback(
            async (_, _) =>
            {
                firstCleanupEntered.TrySetResult();
                await releaseFirstCleanup.Task.ConfigureAwait(false);
            },
            requestId: 97);

        Task? firstDispose = null;
        Task? oldConcurrentDispose = null;
        Task? nextDispose = null;
        Task? nextConcurrentDispose = null;
        try
        {
            firstDispose = first.DisposeAsync().AsTask();
            await firstCleanupEntered.Task.WaitAsync(RaceCoordinationTimeout);
            first.SetBeforeConcurrentDisposeCompletionInstallForTests(() =>
            {
                oldWaiterAtInstall.TrySetResult();
                releaseOldWaiter.Task.GetAwaiter().GetResult();
            });

            oldConcurrentDispose = Task.Run(async () => await first.DisposeAsync());
            await oldWaiterAtInstall.Task.WaitAsync(RaceCoordinationTimeout);

            releaseFirstCleanup.TrySetResult();
            await firstDispose.WaitAsync(RaceCoordinationTimeout);
            var next = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
            Ensure(ReferenceEquals(first, next),
                "the first lease must return before the deliberately delayed old waiter resumes");

            releaseOldWaiter.TrySetResult();
            await oldConcurrentDispose.WaitAsync(RaceCoordinationTimeout);

            next.SetConsumerAbandonedCallback(
                async (_, _) =>
                {
                    nextCleanupEntered.TrySetResult();
                    await releaseNextCleanup.Task.ConfigureAwait(false);
                },
                requestId: 98);
            nextDispose = next.DisposeAsync().AsTask();
            await nextCleanupEntered.Task.WaitAsync(RaceCoordinationTimeout);
            nextConcurrentDispose = next.DisposeAsync().AsTask();

            Ensure(!nextConcurrentDispose.IsCompleted,
                "a stale old-generation completion must be removed so the next lease's concurrent disposer waits for its own cleanup");

            releaseNextCleanup.TrySetResult();
            await Task.WhenAll(nextDispose, nextConcurrentDispose).WaitAsync(RaceCoordinationTimeout);
            Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1 &&
                   !next.HasRetainedReferencesForTests,
                "the recovered next lease must return once without retaining either generation's completion holder");
        }
        finally
        {
            releaseOldWaiter.TrySetResult();
            releaseFirstCleanup.TrySetResult();
            releaseNextCleanup.TrySetResult();
            if (firstDispose is not null)
                await CaptureFailureAsync(firstDispose);
            if (oldConcurrentDispose is not null)
                await CaptureFailureAsync(oldConcurrentDispose);
            if (nextDispose is not null)
                await CaptureFailureAsync(nextDispose);
            if (nextConcurrentDispose is not null)
                await CaptureFailureAsync(nextConcurrentDispose);
            PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        }
    }

    [Test]
    [NotInParallel]
    public async Task RegistrationRetentionShouldPreventUnregisteredDispatcherReuse()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var first = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        var registrationLease = first.RetainForRegistration();
        first.Complete(exception: null);
        await first.DisposeAsync();

        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "an unfinished async registration must retain its dispatcher lease");
        var second = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        Ensure(!ReferenceEquals(first, second),
            "an unregistered dispatcher must not be reused while registration can still resume");

        first.ReleaseRegistrationRetention(registrationLease);
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "releasing the final registration owner should make the terminal dispatcher reusable");
        second.Complete(exception: null);
        await second.DisposeAsync();
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task RegistrationRetentionShouldAllowLateStreamManagerBindingAfterConsumerDispose()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var decodedItems = 0;
        var manager = new StreamManager();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec(beforeDeserialize: () => Interlocked.Increment(ref decodedItems)));
        var registrationLease = dispatcher.RetainForRegistration();
        var registrationReleased = false;
        const long requestId = 96;

        try
        {
            await dispatcher.DisposeAsync();
            Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
                "registration retention must keep a disposed but unregistered dispatcher out of the pool");

            manager.Register(requestId, dispatcher);
            await manager.DispatchChunkAsync(requestId, Payload);
            Ensure(Volatile.Read(ref decodedItems) == 0,
                "late StreamManager binding after consumer disposal must close the entry before it accepts a frame");
            Ensure(manager.ActiveStreamCount == 1,
                "late registration must remain owned by StreamManager until its explicit detach");

            manager.Unregister(requestId);
            Ensure(manager.ActiveStreamCount == 0 &&
                   PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
                "detach alone must not pool the dispatcher while registration retention is still held");

            dispatcher.ReleaseRegistrationRetention(registrationLease);
            registrationReleased = true;
            Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
                "releasing the retained registration owner must return the late-bound dispatcher exactly once");
        }
        finally
        {
            manager.Unregister(requestId);
            if (!registrationReleased)
                dispatcher.ReleaseRegistrationRetention(registrationLease);
            PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        }
    }

    [Test]
    [NotInParallel]
    public async Task RentResetMustFinishBeforeNewLeaseCanBeReturned()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var codec = new ReferenceItemCodec();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
        var delayedOldLease = (IStreamDispatchLease)dispatcher;
        dispatcher.Complete(exception: null);
        await dispatcher.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "the completed old lease must begin in the pool");

        var registrationField = typeof(PooledAsyncStreamDispatcher<ReferenceItem>).GetField(
            "_enumerationCancellationRegistration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("cannot find the dispatcher cancellation registration");
        var leaseStateField = typeof(PooledAsyncStreamDispatcher<ReferenceItem>).GetField(
            "_leaseState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("cannot find the dispatcher lease state");
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var blockingRegistration = cancellation.Token.UnsafeRegister(
            _ =>
            {
                callbackEntered.Set();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("the blocked cancellation callback was not released");
            },
            state: null);
        registrationField.SetValue(dispatcher, blockingRegistration);
        var cancellationTask = Task.Run(cancellation.Cancel);
        Ensure(callbackEntered.Wait(TimeSpan.FromSeconds(3)),
            "the synthetic old cancellation callback must be active");

        PooledAsyncStreamDispatcher<ReferenceItem>? rented = null;
        var rentTask = Task.Run(() =>
        {
            rented = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
        });
        Ensure(SpinWait.SpinUntil(
                () => PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
                TimeSpan.FromSeconds(3)),
            "the new rent must remove the dispatcher from the pool before reset blocks");

        var delayedReturn = Task.Run(delayedOldLease.OnDispatchesDrained);
        try
        {
            var preparingLeaseState = (long)(leaseStateField.GetValue(dispatcher)
                ?? throw new Exception("cannot read the dispatcher lease state"));
            Ensure((preparingLeaseState & 1L) == 0,
                "reset must finish while the dispatcher is still marked returned");
            await delayedReturn.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            releaseCallback.Set();
            await Task.WhenAll(cancellationTask, rentTask, delayedReturn)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }

        var activeDispatcher = rented ?? throw new Exception("the new dispatcher rent did not complete");
        Ensure(ReferenceEquals(dispatcher, activeDispatcher),
            "the new rent must own the prepared dispatcher");
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "a delayed old return must not republish a dispatcher while its new lease is active");

        activeDispatcher.Complete(exception: null);
        await activeDispatcher.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "only disposal of the new lease may return the dispatcher");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentPoolLeasesShouldKeepItemsIsolated()
    {
        PooledAsyncStreamDispatcher<byte>.ClearPoolForTests();
        var codec = new ByteCodec();
        var workers = new Task[64];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                for (var iteration = 0; iteration < 5_000; iteration++)
                {
                    var dispatcher = PooledAsyncStreamDispatcher<byte>.Rent(default, codec);
                    var enumerator = dispatcher.GetAsyncEnumerator();
                    var producer = Task.Run(async () =>
                    {
                        for (byte value = 0; value < 16; value++)
                            await dispatcher.DispatchAsync(new ReadOnlySequence<byte>(new[] { value }));
                        dispatcher.Complete(exception: null);
                    });

                    var count = 0;
                    var sum = 0;
                    while (await enumerator.MoveNextAsync())
                    {
                        count++;
                        sum += enumerator.Current;
                    }
                    await enumerator.DisposeAsync();
                    await producer;
                    Ensure(count == 16 && sum == 120,
                        $"concurrent pooled lease returned count={count}, sum={sum}");
                }
            });
        }

        await Task.WhenAll(workers);
        PooledAsyncStreamDispatcher<byte>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentProducerConsumerShouldDeliverEverySlotExactlyOnceAcrossLeases()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var codec = new ReferenceItemCodec();
        for (var iteration = 0; iteration < 100_000; iteration++)
        {
            var consumedBytes = 0;
            var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
            dispatcher.SetBytesConsumedCallback(
                (_, _, bytes) => Interlocked.Add(ref consumedBytes, bytes),
                iteration + 1,
                0);
            var enumerator = dispatcher.GetAsyncEnumerator();
            var producer = Task.Run(async () =>
            {
                for (var item = 0; item < 256; item++)
                    await dispatcher.DispatchAsync(Payload);
                dispatcher.Complete(exception: null);
            });

            var received = 0;
            while (await enumerator.MoveNextAsync())
                received++;
            await enumerator.DisposeAsync();
            await producer;

            Ensure(received == 256,
                $"iteration {iteration} received {received}/256 stream items (credit={consumedBytes})");
            Ensure(consumedBytes == 256,
                $"iteration {iteration} returned {consumedBytes}/256 bytes of credit");
        }
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task CompleteAllRacingAnIdleConsumerShouldAlwaysReleaseItsWaiter()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var codec = new ReferenceItemCodec();
        var terminal = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "session closed");
        for (var iteration = 0; iteration < 100_000; iteration++)
        {
            var manager = new StreamManager();
            var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
            manager.Register(iteration + 1, 1, dispatcher);
            var enumerator = dispatcher.GetAsyncEnumerator();
            var waiting = enumerator.MoveNextAsync().AsTask();

            manager.CompleteAll(terminal);
            try
            {
                _ = await waiting.WaitAsync(RaceCoordinationTimeout);
                throw new Exception($"iteration {iteration} unexpectedly completed without the terminal error");
            }
            catch (SharpLinkException exception) when (
                exception.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }

            await enumerator.DisposeAsync();
            Ensure(manager.ActiveStreamCount == 0,
                $"iteration {iteration} retained an active stream after CompleteAll");
        }
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    [Test]
    [NotInParallel]
    public async Task LongStreamShouldRecycleSegmentsBeyondBufferedElementLimit()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        var enumerator = dispatcher.GetAsyncEnumerator();
        var received = 0;
        for (var batch = 0; batch < 200; batch++)
        {
            for (var item = 0; item < 100; item++)
                await dispatcher.DispatchAsync(Payload);
            for (var item = 0; item < 100; item++)
            {
                Ensure(await enumerator.MoveNextAsync(), "batched long stream ended early");
                received++;
            }
        }

        dispatcher.Complete(exception: null);
        Ensure(!await enumerator.MoveNextAsync(), "long stream should complete after all batches");
        await enumerator.DisposeAsync();

        Ensure(received == 20_000, "long streams must recycle segments instead of exhausting lifetime capacity");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void CompleteAndDisposeWhileAttached(ReferenceItemCodec codec)
    {
        var dispatcher = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);
        var lease = (IStreamDispatchLease)dispatcher;
        var dispatchState = new AttachedDispatchState();
        lease.BindDispatchState(dispatchState);
        dispatcher.Complete(exception: null);
        dispatcher.DisposeAsync().GetAwaiter().GetResult();
        dispatchState.Detach();
        lease.OnDispatchesDrained();
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureDispatchFailureAsync(Func<ValueTask> dispatch)
    {
        try
        {
            await dispatch();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool IsFreeSegmentStackEmpty(PooledAsyncStreamDispatcher<ReferenceItem> dispatcher)
    {
        var freeSegments = GetPrivateField(dispatcher, "_freeSegments");
        var isEmpty = freeSegments.GetType().GetProperty(
            "IsEmpty",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new Exception("cannot find the free-segment stack IsEmpty property");
        return (bool)(isEmpty.GetValue(freeSegments)
            ?? throw new Exception("cannot read the free-segment stack IsEmpty property"));
    }

    private static object GetOnlyFreeSegment(PooledAsyncStreamDispatcher<ReferenceItem> dispatcher)
    {
        var freeSegments = (System.Collections.IEnumerable)GetPrivateField(dispatcher, "_freeSegments");
        var enumerator = freeSegments.GetEnumerator();
        try
        {
            Ensure(enumerator.MoveNext(), "the test must create a recycled free segment");
            var segment = enumerator.Current
                ?? throw new Exception("the free-segment stack returned a null segment");
            Ensure(!enumerator.MoveNext(), "the test must create exactly one recycled free segment");
            return segment;
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = GetPrivateFieldInfo(target, fieldName);
        return field.GetValue(target)
            ?? throw new Exception($"private field {fieldName} was null");
    }

    private static System.Reflection.FieldInfo GetPrivateFieldInfo(object target, string fieldName)
        => target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public)
            ?? throw new Exception($"cannot find private field {fieldName}");

    private static void SeedStaleSegmentState(object segment)
    {
        var items = (ReferenceItem[])GetPrivateField(segment, "Items");
        var encodedByteCounts = (int[])GetPrivateField(segment, "EncodedByteCounts");
        var published = GetPrivateFieldInfo(segment, "Published");
        var next = GetPrivateFieldInfo(segment, "Next");

        items[0] = new ReferenceItem(new object());
        encodedByteCounts[0] = 7;
        published.SetValue(segment, 1);
        next.SetValue(segment, segment);
    }

    private static void EnsureSegmentStateWasCleared(object segment, string segmentName)
    {
        var items = (ReferenceItem[])GetPrivateField(segment, "Items");
        var encodedByteCounts = (int[])GetPrivateField(segment, "EncodedByteCounts");
        var published = GetPrivateFieldInfo(segment, "Published");
        var next = GetPrivateFieldInfo(segment, "Next");

        Ensure(items[0] is null, $"{segmentName} must clear stale item references");
        Ensure(encodedByteCounts[0] == 0, $"{segmentName} must clear stale byte counts");
        Ensure((int)(published.GetValue(segment) ?? -1) == 0,
            $"{segmentName} must reset its published count");
        Ensure(next.GetValue(segment) is null, $"{segmentName} must clear stale links");
    }

    private sealed record ReferenceItem(object Marker);

    private sealed class ReferenceItemCodec(
        object? marker = null,
        Action? beforeDeserialize = null) : IRpcCodec<ReferenceItem>
    {
        private readonly object _marker = marker ?? new object();

        public void Serialize(in ReferenceItem value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(1);
            span[0] = 1;
            buffer.Advance(1);
        }

        public ReferenceItem Deserialize(in ReadOnlySequence<byte> buffer)
        {
            beforeDeserialize?.Invoke();
            return new ReferenceItem(_marker);
        }
    }

    private sealed class ByteCodec : IRpcCodec<byte>
    {
        public void Serialize(in byte value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(1);
            span[0] = value;
            buffer.Advance(1);
        }

        public byte Deserialize(in ReadOnlySequence<byte> buffer) => buffer.FirstSpan[0];
    }

    private sealed class GatedCloseDispatchState : IStreamDispatchState
    {
        private readonly TaskCompletionSource _firstCloseEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstClose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstClose;
        private int _closeCount;
        private int _firstCloseReleased;
        private int _closedConcurrently;

        internal GatedCloseDispatchState(bool blockFirstClose = true)
        {
            _blockFirstClose = blockFirstClose;
        }

        internal Task FirstCloseEntered => _firstCloseEntered.Task;

        internal bool IsClosed => Volatile.Read(ref _closeCount) != 0;

        internal int CloseCount => Volatile.Read(ref _closeCount);

        internal bool WasClosedConcurrently => Volatile.Read(ref _closedConcurrently) != 0;

        public bool HasActiveDispatches => false;

        public bool IsDetached => true;

        public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public void Close()
        {
            if (Interlocked.Increment(ref _closeCount) != 1)
            {
                if (Volatile.Read(ref _firstCloseReleased) == 0)
                    Volatile.Write(ref _closedConcurrently, 1);
                return;
            }

            _firstCloseEntered.TrySetResult();
            if (_blockFirstClose)
                _releaseFirstClose.Task.GetAwaiter().GetResult();
        }

        internal void ReleaseFirstClose()
        {
            Volatile.Write(ref _firstCloseReleased, 1);
            _releaseFirstClose.TrySetResult();
        }
    }

    private sealed class AttachedDispatchState : IStreamDispatchState
    {
        private int _closeCount;
        private int _detached;

        internal int CloseCount => Volatile.Read(ref _closeCount);

        public bool HasActiveDispatches => false;

        public bool IsDetached => Volatile.Read(ref _detached) != 0;

        public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public void Close() => Interlocked.Increment(ref _closeCount);

        internal void Detach() => Volatile.Write(ref _detached, 1);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _continuations = [];
        private int _postCount;

        internal int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _continuations.Enqueue((callback, state));
            Interlocked.Increment(ref _postCount);
        }
    }

    private sealed class CoordinatedPoolReturnState : IStreamDispatchState, IDisposable
    {
        private readonly ManualResetEventSlim _bothPrechecksEntered = new();
        private readonly ManualResetEventSlim _releaseDelayedReturn = new();
        private readonly TaskCompletionSource _detached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _coordinateReturns;
        private int _detachedReads;

        public bool HasActiveDispatches => false;

        public bool IsDetached
        {
            get
            {
                if (Volatile.Read(ref _coordinateReturns) == 0)
                    return false;

                switch (Interlocked.Increment(ref _detachedReads))
                {
                    case 1:
                        if (!_bothPrechecksEntered.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The second pool-return contender did not enter its precheck.");
                        return true;
                    case 2:
                        _bothPrechecksEntered.Set();
                        if (!_releaseDelayedReturn.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The delayed pool-return contender was not released.");
                        return true;
                    default:
                        return true;
                }
            }
        }

        public void Close()
        {
        }

        public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
            => cancellationToken.CanBeCanceled
                ? new ValueTask(_detached.Task.WaitAsync(cancellationToken))
                : new ValueTask(_detached.Task);

        public void CoordinateReturns()
        {
            Volatile.Write(ref _coordinateReturns, 1);
            _detached.TrySetResult();
        }

        public bool WaitForBothPrechecks(TimeSpan timeout) => _bothPrechecksEntered.Wait(timeout);

        public void ReleaseDelayedReturn() => _releaseDelayedReturn.Set();

        public void Dispose()
        {
            _bothPrechecksEntered.Dispose();
            _releaseDelayedReturn.Dispose();
        }
    }

    private sealed class NullReferenceItemCodec : IRpcCodec<ReferenceItem>
    {
        public void Serialize(in ReferenceItem value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException();

        public ReferenceItem Deserialize(in ReadOnlySequence<byte> buffer) => null!;
    }
}
