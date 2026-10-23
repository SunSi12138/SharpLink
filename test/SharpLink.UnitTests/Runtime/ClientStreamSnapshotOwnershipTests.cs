using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public class ClientStreamSnapshotOwnershipTests
{
    [Test]
    public async Task ReleaseFailureMustReleaseOnceAndReturnUnpublishedWriter()
        => await VerifyAsync(releaseThrows: true);

    [Test]
    public async Task SerializerFailureMustRemainPrimaryWhenReleaseAlsoFails()
        => await VerifyAsync(serializeThrows: true, releaseThrows: true);

    [Test]
    public async Task SerializerCancellationMustRemainPrimaryWhenReleaseAlsoFails()
        => await VerifyAsync(serializeThrows: true, cancelDuringSerialization: true, releaseThrows: true);

    [Test]
    public async Task CancellationBeforeCreditMustRemainPrimaryWhenReleaseFails()
        => await VerifyAsync(cancelBeforeCredit: true, releaseThrows: true);

    [Test]
    public async Task NormalSnapshotMustReleaseOnceAndPublish()
        => await VerifyAsync();

    [Test]
    public async Task NullSnapshotMustNotReleaseAndMustPublish()
        => await VerifyAsync(nullSnapshot: true);

    [Test]
    public async Task SerializerFailureMustReleaseSnapshotAndReturnWriter()
        => await VerifyAsync(serializeThrows: true);

    [Test]
    public async Task CancellationAfterSerializationMustReleaseAndNotPublish()
        => await VerifyAsync(cancelDuringSerialization: true);

    [Test]
    public async Task CancellationBeforeCreditMustReleaseWithoutRentingWriter()
        => await VerifyAsync(cancelBeforeCredit: true);

    [Test]
    public async Task SerializerFailureWithNullSnapshotMustReturnWriterWithoutRelease()
        => await VerifyAsync(serializeThrows: true, nullSnapshot: true);

    private static async Task VerifyAsync(
        bool serializeThrows = false,
        bool releaseThrows = false,
        bool cancelDuringSerialization = false,
        bool cancelBeforeCredit = false,
        bool nullSnapshot = false)
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        using var cancellation = new CancellationTokenSource();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "client-snapshot-ownership", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions(context), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 16, connectionReceiveWindowBytes: 64);
        var controller = GetFlowController(session);
        Exception? serializationFailure = serializeThrows
            ? cancelDuringSerialization
                ? new OperationCanceledException("original serializer cancellation", cancellation.Token)
                : new InvalidOperationException("original serialization failure")
            : null;
        var releaseFailure = new InvalidOperationException("first snapshot release failure");
        var codec = new TrackingCodec(nullSnapshot, serializationFailure,
            releaseThrows ? releaseFailure : null,
            cancelBeforeCredit ? cancellation.Cancel : null,
            cancelDuringSerialization ? cancellation.Cancel : null);
        var expectPublished = !serializeThrows && !releaseThrows &&
            !cancelDuringSerialization && !cancelBeforeCredit;
        Exception? observed = null;
        try
        {
            try
            {
                await session.SendClientStreamChunkAsync(71, 1, 7, codec, codec,
                    default, TimeProvider.System, cancellation.Token)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                observed = exception;
            }

            // On success the send pump owns the writer; flush before inspecting final pool
            // ownership. On failure nothing may be enqueued, so inspect before any other rent.
            if (expectPublished)
                await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var writerReturned = cancelBeforeCredit ? codec.Writer is null : IsInactive(codec.Writer);
            var poolCopies = codec.Writer is null ? 0 : CountPoolCopies(context.Buffers, codec.Writer);
            var primaryPreserved = serializationFailure is not null
                ? ReferenceEquals(observed, serializationFailure)
                : cancelBeforeCredit || cancelDuringSerialization
                    ? observed is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token
                    : releaseThrows ? ReferenceEquals(observed, releaseFailure) : observed is null;
            var releaseCallsExpected = nullSnapshot ? 0 : 1;
            Ensure(primaryPreserved && codec.ReleaseCalls == releaseCallsExpected && writerReturned &&
                poolCopies == (cancelBeforeCredit ? 0 : 1),
                $"primaryPreserved={primaryPreserved}, releaseCalls={codec.ReleaseCalls}, " +
                $"writerReturned={writerReturned}, poolCopies={poolCopies}, observed={observed?.Message}");
            Ensure(codec.SerializeCalls == (cancelBeforeCredit ? 0 : 1),
                "credit cancellation must not invoke serialization");
            Ensure(controller.SendConnectionCredit == (expectPublished ? 60 : 64),
                "only published bytes may retain connection credit");
            Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
                "the sized path must not retain a pre-credit owner or waiter");

            // A marker makes absence of StreamData observable without timing a negative read.
            await session.SendRpcErrorWithBackpressureAsync(999,
                new SharpLinkException(SharpLinkErrorCode.Internal, "marker"));
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var remaining = read.Buffer;
            var streamDataCount = 0;
            var responseCount = 0;
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, context.Protocol, out var header, out _))
            {
                if (header.Type == ProtocolV2FrameType.StreamData)
                    streamDataCount++;
                else if (header.Type == ProtocolV2FrameType.Response)
                    responseCount++;
                else
                    throw new InvalidOperationException($"Unexpected frame: {header.Type}");
            }
            Ensure(remaining.IsEmpty && responseCount == 1 && streamDataCount == (expectPublished ? 1 : 0),
                "only a successful item may publish StreamData");
            output.Reader.AdvanceTo(read.Buffer.End);
            if (expectPublished)
                controller.ApplyWindowUpdate(71, 1, sizeof(int));
            session.CompleteSendStream(71, 1);
            Ensure(controller.SendConnectionCredit == 64 && controller.ActiveSendStreamCount == 0 &&
                controller.RetainedSendStreamCount == 0,
                "settlement must retire all stream state and restore exact connection credit");
        }
        finally
        {
            // Baseline may leak. Dispose only after the tested observations and assertions.
            codec.Writer?.Dispose();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private static StreamFlowController GetFlowController(RpcSession session)
    {
        var field = typeof(RpcSession).GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RpcSession._protocolState was not found.");
        return ((RpcSessionProtocolState)field.GetValue(session)!).FlowController
            ?? throw new InvalidOperationException("The test session has no flow controller.");
    }

    private static int CountPoolCopies(SharpLinkBufferWriterPool pool, IRpcByteBufferWriter writer)
    {
        var field = typeof(SharpLinkBufferWriterPool).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Writer pool storage was not found.");
        return ((ConcurrentQueue<PooledByteBufferWriter>)field.GetValue(pool)!)
            .Count(candidate => ReferenceEquals(candidate, writer));
    }

    private static bool IsInactive(IRpcByteBufferWriter? writer)
    {
        if (writer is null)
            return false;
        try
        {
            _ = writer.WrittenCount;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Client snapshot ownership assertion failed: {message}.");
    }

    private sealed class SnapshotToken : IRpcSizedCodecSnapshot { }

    private sealed class TrackingCodec(bool nullSnapshot, Exception? serializationFailure,
        Exception? releaseFailure, Action? onSize, Action? onSerialize) : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        private readonly IRpcSizedCodecSnapshot? _snapshot = nullSnapshot ? null : new SnapshotToken();
        internal IRpcByteBufferWriter? Writer { get; private set; }
        internal int SerializeCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        public bool CanExactSize => true;

        public bool TryGetEncodedSize(in int value, out int size)
        {
            size = sizeof(int);
            return true;
        }

        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            size = sizeof(int);
            snapshot = _snapshot;
            onSize?.Invoke();
            return true;
        }

        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(ReferenceEquals(snapshot, _snapshot), "serializer must receive the original snapshot");
            SerializeCalls++;
            Writer = (IRpcByteBufferWriter)buffer;
            BitConverter.TryWriteBytes(buffer.GetSpan(sizeof(int)), value);
            buffer.Advance(sizeof(int));
            onSerialize?.Invoke();
            if (serializationFailure is not null)
                throw serializationFailure;
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(_snapshot is not null && ReferenceEquals(snapshot, _snapshot),
                "release must receive the owned non-null snapshot");
            ReleaseCalls++;
            if (releaseFailure is not null)
                throw ReleaseCalls == 1 ? releaseFailure : new InvalidOperationException("repeated snapshot release failure");
        }

        public void Serialize(in int value, IBufferWriter<byte> buffer)
            => throw new InvalidOperationException("Unexpected unsized serialization.");

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);
    }
}
