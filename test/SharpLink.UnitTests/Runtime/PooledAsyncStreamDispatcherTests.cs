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
        await first.DisposeAsync();

        var second = PooledAsyncStreamDispatcher<ReferenceItem>.Rent(
            default,
            new ReferenceItemCodec());
        Ensure(!ReferenceEquals(first, second),
            "dispatcher with an in-flight producer must not be rented to another stream");

        release.Set();
        await producer.WaitAsync(TimeSpan.FromSeconds(3));
        second.Complete(exception: null);
        await second.DisposeAsync();
        Ensure(PooledAsyncStreamDispatcher<ReferenceItem>.RetainedCountForTests == 2,
            "both dispatchers should become reusable after the producer exits");
        PooledAsyncStreamDispatcher<ReferenceItem>.ClearPoolForTests();
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
}
