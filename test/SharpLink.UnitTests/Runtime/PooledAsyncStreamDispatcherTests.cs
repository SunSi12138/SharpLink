using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PooledAsyncStreamDispatcherTests
{
    private static readonly ReadOnlySequence<byte> Payload = new(new byte[] { 1 });

    [Test]
    [NotInParallel]
    public async Task PoolShouldRetainAtMost1024DispatchersAfterBurst()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var codec = new ReferenceItemCodec();
        var dispatchers = new PooledAsyncStreamDispatcher<ReferenceItem>[10_000];
        for (var index = 0; index < dispatchers.Length; index++)
            dispatchers[index] = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, codec);

        for (var index = 0; index < dispatchers.Length; index++)
        {
            dispatchers[index].Complete(exception: null);
            await dispatchers[index].DisposeAsync();
        }

        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1024,
            "pool retention must be bounded after a 10,000-stream burst");
        var discarded = new WeakReference(dispatchers[^1]);
        Array.Clear(dispatchers);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Ensure(!discarded.IsAlive, "dispatchers above the retention cap must remain collectible");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
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
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
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
    public async Task RegistrationRetentionShouldPreventUnregisteredDispatcherReuse()
    {
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
        var first = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        first.RetainForRegistration();
        first.Complete(exception: null);
        await first.DisposeAsync();

        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 0,
            "an unfinished async registration must retain its dispatcher lease");
        var second = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(default, new ReferenceItemCodec());
        Ensure(!ReferenceEquals(first, second),
            "an unregistered dispatcher must not be reused while registration can still resume");

        first.ReleaseRegistrationRetention();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 1,
            "releasing the final registration owner should make the terminal dispatcher reusable");
        second.Complete(exception: null);
        await second.DisposeAsync();
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
                _ = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
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
}
