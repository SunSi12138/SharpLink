namespace SharpLink.UnitTests.Runtime;

public class InboundStreamAbandonmentTests
{
    private static IRpcCodecProvider SCodecs => RpcSessionTestFixture.RuntimeContext.Codecs;

    [Test]
    public async Task AbandonBeforeTypedAttachShouldReleaseBufferedCreditAndKeepRouteUntilPeerTerminal()
    {
        const long requestId = 30401;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));

        Ensure(counters.Accepted == 4, "deferred bytes should be flow-control accepted");
        Ensure(counters.Consumed == 0, "deferred bytes stay outstanding while the invocation may still attach");
        Ensure(manager.ActiveStreamCount == 1, "the inbound route is active before abandonment");

        manager.AbandonExistingRequestStreams(requestId, 1);

        Ensure(counters.Consumed == 4, "abandonment returns credit for deferred buffered bytes");
        Ensure(manager.ActiveStreamCount == 1, "abandonment must keep the receive route alive");

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[] { 5, 6, 7 }));

        Ensure(counters.Accepted == 7 && counters.Consumed == 7,
            "post-abandonment frames should be accepted and immediately consumed");
        Ensure(manager.DroppedStreamFrames == 0,
            "the abandoned stream must remain routed rather than become an unknown stream");

        manager.CompletePeerStream(requestId, streamId, exception: null);

        Ensure(manager.ActiveStreamCount == 0, "peer terminal should retire the abandoned route");
        Ensure(counters.Completed == 1, "peer terminal should publish receive completion exactly once");
    }

    [Test]
    public async Task AbandonAfterTypedAttachShouldDisposeTypedBufferAndContinueDiscarding()
    {
        const long requestId = 30402;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        var typed = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
        manager.Register(requestId, streamId, typed);

        var payload = SerializeInt(42);
        var encodedBytes = payload.Length;
        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(payload));

        Ensure(counters.Accepted == encodedBytes, "typed frame should be accepted");
        Ensure(counters.Consumed == 0, "typed buffered item should hold receive credit before abandonment");

        manager.AbandonExistingRequestStreams(requestId, 1);

        Ensure(counters.Consumed == encodedBytes,
            "typed dispatcher disposal should discard its buffered item and return exact credit");
        Ensure(manager.ActiveStreamCount == 1,
            "disposing the typed child must not close the stable inbound route");

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(payload));

        Ensure(counters.Accepted == encodedBytes * 2 && counters.Consumed == encodedBytes * 2,
            "future peer frames should bypass the disposed typed child and return credit immediately");
        Ensure(manager.DroppedStreamFrames == 0,
            "typed-attached abandonment should not turn later frames into unknown-stream drops");

        manager.CompletePeerStream(requestId, streamId, exception: null);

        Ensure(manager.ActiveStreamCount == 0, "peer terminal should retire the drain route");
        Ensure(counters.Completed == 1, "peer terminal should publish receive completion exactly once");
    }

    [Test]
    public async Task PeerTerminalBeforeLocalCompletionShouldRetainAndDisposeTypedRoute()
    {
        const long requestId = 30403;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        var typed = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
        manager.Register(requestId, streamId, typed);

        var payload = SerializeInt(42);
        var encodedBytes = payload.Length;
        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(payload));

        Ensure(counters.Accepted == encodedBytes && counters.Consumed == 0,
            "typed buffered item should hold receive credit before peer terminal");

        manager.CompletePeerStream(requestId, streamId, exception: null);

        Ensure(manager.ActiveStreamCount == 1,
            "peer terminal must retain a OneWay route until local invocation completion");
        Ensure(counters.Completed == 1,
            "peer terminal should flush receive-credit terminal state immediately");
        Ensure(counters.Consumed == 0,
            "peer terminal alone must not abandon a typed buffer still owned by the handler");

        manager.AbandonExistingRequestStreams(requestId, 1);

        Ensure(counters.Consumed == encodedBytes,
            "local completion must dispose the retained typed buffer and return late credit");
        Ensure(manager.ActiveStreamCount == 0,
            "the second terminal signal should retire the stable receive route");
        Ensure(counters.Completed == 1,
            "route retirement must not publish receive terminal twice");
    }

    [Test]
    public async Task PeerTerminalBeforeTypedAttachShouldRetainThroughAttachUntilLocalCompletion()
    {
        const long requestId = 30404;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        var payload = SerializeInt(42);
        var encodedBytes = payload.Length;
        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(payload));

        manager.CompletePeerStream(requestId, streamId, exception: null);
        Ensure(manager.ActiveStreamCount == 1 && counters.Completed == 1,
            "peer terminal must publish flow terminal but retain a pre-attach OneWay route");
        Ensure(counters.Consumed == 0,
            "pre-attach bytes must remain owned until typed attachment/local abandonment");

        var typed = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
        manager.Register(requestId, streamId, typed);

        Ensure(manager.ActiveStreamCount == 1,
            "typed attachment after peer terminal must not retire the retained parent route");
        Ensure(counters.Consumed == 0,
            "the replayed typed item must remain handler-owned until local completion");

        manager.AbandonExistingRequestStreams(requestId, 1);

        Ensure(counters.Consumed == encodedBytes,
            "local completion must dispose the post-terminal typed buffer and return late credit");
        Ensure(manager.ActiveStreamCount == 0,
            "local completion after peer terminal and typed attach should retire the route");
        Ensure(counters.Completed == 1,
            "receive terminal must remain single-publication across final route cleanup");
    }

    [Test]
    public async Task PromotionShouldCarryOneWayLocalRetentionIntoExistingAdmissionRoute()
    {
        const long requestId = 30405;
        const ushort streamId = 1;
        var counters = new Counters();
        var queuedBytes = 0;
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            bytes =>
            {
                queuedBytes += bytes;
                return true;
            },
            bytes => queuedBytes -= bytes,
            static () => { });

        var payload = SerializeInt(42);
        var encodedBytes = payload.Length;
        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(payload));
        Ensure(queuedBytes == encodedBytes, "admission route should initially own retained bytes");

        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);
        Ensure(queuedBytes == 0,
            "active promotion must settle admission queue-byte ownership before invocation");

        var typed = PooledAsyncStreamDispatcher<int>.Rent(default, SCodecs);
        manager.Register(requestId, streamId, typed);
        manager.CompletePeerStream(requestId, streamId, exception: null);

        Ensure(manager.ActiveStreamCount == 1,
            "promoted OneWay retention must keep the typed route after peer terminal");
        Ensure(counters.Completed == 1 && counters.Consumed == 0,
            "peer terminal should publish while the typed buffer remains locally owned");

        manager.AbandonExistingRequestStreams(requestId, 1);
        Ensure(counters.Consumed == encodedBytes && manager.ActiveStreamCount == 0,
            "local completion must dispose the promoted typed buffer and retire the route");
    }

    [Test]
    public async Task LocalCompletionShouldNotRetireAbandonedRouteBeforePeerTerminal()
    {
        const long requestId = 30406;
        const ushort streamId = 1;
        var counters = new Counters();
        var manager = CreateManager(counters);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));
        Ensure(counters.Accepted == 4 && counters.Consumed == 0,
            "deferred bytes should remain outstanding before local completion");

        manager.CompleteStream(
            requestId,
            streamId,
            new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "local retention failure"));
        Ensure(manager.ActiveStreamCount == 1,
            "local completion state must not be mistaken for peer StreamComplete");
        Ensure(counters.Completed == 0,
            "local completion on a retained drain route must defer receive terminal to peer StreamComplete");

        manager.AbandonExistingRequestStreams(requestId, 1);
        Ensure(manager.ActiveStreamCount == 1,
            "local abandonment after a local completion must still retain the drain route");
        Ensure(counters.Consumed == 4,
            "local abandonment should release deferred bytes even after local completion");
        Ensure(counters.Completed == 0,
            "abandonment must not publish receive terminal before the peer terminal arrives");

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[] { 5, 6, 7 }));
        Ensure(counters.Accepted == 7 && counters.Consumed == 7,
            "the route must continue discarding and returning credit until peer terminal");
        Ensure(manager.DroppedStreamFrames == 0,
            "local completion must not turn later peer frames into unknown-stream drops");

        manager.CompletePeerStream(requestId, streamId, exception: null);
        Ensure(manager.ActiveStreamCount == 0,
            "only the actual peer terminal should retire the locally completed abandoned route");
        Ensure(counters.Completed == 1,
            "receive terminal publication should remain idempotent across local and peer terminal states");
    }

    private static byte[] SerializeInt(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        SCodecs.GetCodec<int>().Serialize(value, writer);
        return writer.WrittenMemory.ToArray();
    }

    private static StreamManager CreateManager(Counters counters)
        => new(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => counters.Accepted += bytes,
            (_, _, bytes) => counters.Consumed += bytes,
            (_, _) => counters.Completed++);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Counters
    {
        internal int Accepted;
        internal int Consumed;
        internal int Completed;
    }
}
