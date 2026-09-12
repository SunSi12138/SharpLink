using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{

    [Test]
    public async Task PreAdmissionCapacityDropShouldReturnAcceptedReceiveCredit()
    {
        var accepted = 0;
        var consumed = 0;
        var capacityExceeded = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            50,
            1,
            buffers,
            _ => false,
            _ => throw new InvalidOperationException("No bytes were reserved."),
            () => capacityExceeded++);

        await manager.DispatchChunkAsync(
            50,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));

        Ensure(accepted == 4, "pre-admission bytes accepted");
        Ensure(consumed == 4, "dropped pre-admission bytes returned as receive credit");
        Ensure(capacityExceeded == 1, "pre-admission capacity callback");
        manager.CompleteRequestStreams(50, exception: null);
        Ensure(manager.ActiveStreamCount == 0, "capacity-dropped stream reclaimed");
    }


    [Test]
    public async Task CompletedPreAdmissionStreamShouldRetireAfterDispatcherAttach()
    {
        var released = 0;
        var completed = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            (_, _) => completed++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            51,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            51,
            1,
            new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 }));
        manager.CompleteStream(51, 1, exception: null);
        var dispatcher = new RecordingDispatcher();

        manager.Register(51, 1, dispatcher);

        Ensure(dispatcher.DispatchCount == 1, "buffered pre-admission item dispatched");
        Ensure(dispatcher.CompleteCount == 1, "early completion forwarded on attach");
        Ensure(released == 3, "buffered pre-admission bytes released");
        Ensure(completed == 1, "stream completion callback invoked once");
        Ensure(manager.ActiveStreamCount == 0, "completed pre-admission stream reclaimed");
    }


    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreAdmissionCompletionDuringRetentionShouldReturnReceiveCredit(bool compressed)
    {
        const long requestId = 56;
        const ushort streamId = 1;
        var accepted = 0;
        var consumed = 0;
        var released = 0;
        var decoded = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            _ =>
            {
                manager.CompleteStream(requestId, streamId, exception: null);
                return true;
            },
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."),
            _ =>
            {
                decoded++;
                throw new InvalidOperationException("A completed pre-admission stream must not decode its frame.");
            });

        if (compressed)
        {
            Ensure(manager.TryDispatchPreAdmissionCompressed(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[] { 4, 5, 6 }),
                originalByteCount: 17,
                out var dispatch),
                "compressed pre-admission frame intercepted");
            await dispatch;
        }
        else
        {
            await manager.DispatchChunkAsync(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));
        }

        var expectedCredit = compressed ? 17 : 4;
        Ensure(accepted == expectedCredit && consumed == expectedCredit,
            "completion race returns the exact accepted receive credit");
        Ensure(released == (compressed ? 3 : 4), "completion race releases retained wire bytes");
        Ensure(decoded == 0, "completed compressed frame is discarded before decode");
        manager.Register(requestId, streamId, new RecordingDispatcher());
        Ensure(manager.ActiveStreamCount == 0, "completed pre-admission stream reclaimed after attach");
    }


    [Test]
    public async Task RejectedStreamDrainerShouldReturnCreditAndRetireOnComplete()
    {
        var accepted = 0;
        var consumed = 0;
        var completed = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            (_, _) => completed++);
        manager.DrainRejectedRequestStreams(52, 1);

        await manager.DispatchChunkAsync(
            52,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));
        Ensure(manager.TryDispatchPreAdmissionCompressed(
            52,
            1,
            new ReadOnlySequence<byte>(new byte[] { 9, 9 }),
            originalByteCount: 11,
            out var compressedDispatch),
            "discarding stream should intercept compressed frames before decode");
        await compressedDispatch;
        manager.CompleteStream(52, 1, exception: null);

        Ensure(accepted == 14 && consumed == 14,
            "discarded raw and compressed bytes return exact original credit");
        Ensure(completed == 1, "discarded stream completion callback");
        Ensure(manager.ActiveStreamCount == 0, "discarded stream reclaimed");
    }


    [Test]
    public async Task FailureDrainerShouldNotReplaceAnAttachedGeneratedDispatcher()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        manager.Register(55, 1, dispatcher);

        manager.DrainRejectedRequestStreams(55, 1);
        await manager.DispatchChunkAsync(
            55,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1, 2 }));

        Ensure(dispatcher.DispatchCount == 1, "existing generated dispatcher remains active");
        Ensure(manager.ActiveStreamCount == 1, "ignored drainer does not change active accounting");
        manager.CompleteStream(55, 1, exception: null);
        Ensure(manager.ActiveStreamCount == 0, "existing dispatcher reclaimed once");
    }


    [Test]
    public async Task RejectedQueuedCompressedStreamShouldNotInvokeDecoder()
    {
        var accepted = 0;
        var consumed = 0;
        var released = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            53,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."),
            _ => throw new InvalidOperationException("Rejected compressed frames must not be decoded."));
        Ensure(manager.TryDispatchPreAdmissionCompressed(
            53,
            1,
            new ReadOnlySequence<byte>(new byte[] { 4, 5, 6, 7 }),
            originalByteCount: 32,
            out var queuedDispatch),
            "compressed pre-admission frame intercepted");
        await queuedDispatch;

        manager.DrainRejectedRequestStreams(53, 1);
        await manager.DispatchChunkAsync(
            53,
            1,
            new ReadOnlySequence<byte>(new byte[] { 8, 9, 10 }));
        manager.CompleteStream(53, 1, exception: null);

        Ensure(accepted == 35 && consumed == 35,
            "queued rejection returns buffered and future frame credit");
        Ensure(released == 4, "queued rejected compressed wire bytes released");
        Ensure(manager.ActiveStreamCount == 0, "queued rejected compressed stream reclaimed");
    }


    [Test]
    public async Task ReplayFailureShouldReleaseEveryRemainingPreAdmissionItem()
    {
        var accepted = 0;
        var consumed = 0;
        var retained = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes,
            (_, _, bytes) => consumed += bytes,
            null);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            54,
            1,
            buffers,
            bytes =>
            {
                retained += bytes;
                return true;
            },
            bytes => retained -= bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        for (var value = 1; value <= 3; value++)
        {
            await manager.DispatchChunkAsync(
                54,
                1,
                new ReadOnlySequence<byte>(new byte[] { checked((byte)value) }));
        }

        try
        {
            manager.Register(54, 1, new ThrowingReplayDispatcher());
            throw new Exception("expected replay failure");
        }
        catch (InvalidDataException)
        {
        }

        Ensure(retained == 0, "failed replay should release every retained owner");
        Ensure(accepted == 3 && consumed == 3,
            "failed replay should return credit for the failed and unvisited items");
        manager.DrainRejectedRequestStreams(54, 1);
        await manager.DispatchChunkAsync(
            54,
            1,
            new ReadOnlySequence<byte>(new byte[] { 4 }));
        manager.CompleteStream(54, 1, exception: null);
        Ensure(accepted == 4 && consumed == 4,
            "failed replay should recover in place as a credit-returning drainer");
        Ensure(manager.ActiveStreamCount == 0, "failed replay stream reclaimed");
    }


    [Test]
    public async Task PreAdmissionAttachShouldNotBlockOnAsynchronousReplay()
    {
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            57,
            1,
            buffers,
            _ => true,
            _ => { },
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            57,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        var dispatcher = new OrderedReplayDispatcher();

        var registration = Task.Run(() => manager.Register(57, 1, dispatcher));
        await dispatcher.FirstEntered.WaitAsync(RaceCoordinationTimeout);
        var returnedBeforeRelease = false;
        try
        {
            await registration.WaitAsync(TimeSpan.FromMilliseconds(200));
            returnedBeforeRelease = true;
        }
        catch (TimeoutException)
        {
        }

        ValueTask liveDispatch = default;
        if (returnedBeforeRelease)
        {
            liveDispatch = manager.DispatchChunkAsync(
                57,
                1,
                new ReadOnlySequence<byte>(new byte[] { 2 }));
            Ensure(liveDispatch.IsCompletedSuccessfully,
                "a live frame is retained without blocking the transport reader");
            Ensure(dispatcher.EnteredValues.SequenceEqual([(byte)1]),
                "a live frame must not overtake retained replay");
        }
        dispatcher.ReleaseFirst();
        await registration.WaitAsync(RaceCoordinationTimeout);
        if (returnedBeforeRelease)
            await dispatcher.SecondEntered.WaitAsync(RaceCoordinationTimeout);

        Ensure(returnedBeforeRelease,
            "dispatcher registration must not synchronously wait for asynchronous replay");
        Ensure(dispatcher.EnteredValues.SequenceEqual([(byte)1, (byte)2]),
            "retained and live frames preserve wire order");
        manager.CompleteStream(57, 1, exception: null);
    }


    [Test]
    public async Task PreAdmissionAttachCallbacksShouldRunOutsideRequestRegistryLock()
    {
        const long requestId = 58;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            _ => true,
            _ => { },
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        var dispatcher = new ReentrantConfigurationDispatcher(manager, requestId);

        await Task.Run(() => manager.Register(requestId, 1, dispatcher))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(!dispatcher.RegistryLockWasHeld,
            "dispatcher callbacks must execute without the request registry lock");
        manager.CompleteRequestStreams(requestId, exception: null);
    }


    [Test]
    public async Task CompletionDuringAsynchronousReplayShouldFollowRetainedFrames()
    {
        var released = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            59,
            1,
            buffers,
            _ => true,
            bytes => released += bytes,
            () => throw new InvalidOperationException("Capacity should not be exhausted."));
        await manager.DispatchChunkAsync(
            59,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        var dispatcher = new OrderedReplayDispatcher();

        manager.Register(59, 1, dispatcher);
        await dispatcher.FirstEntered.WaitAsync(RaceCoordinationTimeout);
        manager.CompleteStream(59, 1, exception: null);

        Ensure(dispatcher.CompleteCount == 0,
            "completion must wait until retained replay exits");
        Ensure(manager.ActiveStreamCount == 0,
            "the completed registry entry retires while replay owns its lease");
        dispatcher.ReleaseFirst();
        await dispatcher.Completed.WaitAsync(RaceCoordinationTimeout);

        Ensure(dispatcher.CompleteCount == 1,
            "completion is forwarded once after replay");
        Ensure(released == 1,
            "retained storage is released before completion finishes");
    }
}
