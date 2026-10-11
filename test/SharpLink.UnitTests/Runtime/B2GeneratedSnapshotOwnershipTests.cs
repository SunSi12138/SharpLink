using System.IO.Pipelines;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

// Uses only the pre-B2 API so the same fixture can run against f8ba891 / ae57b4a
// and an independent ownership fix. These are not B2 performance tests.
public class StreamFlowGrantSnapshotOwnershipTests
{
    [Test]
    public async Task SnapshotReleaseFailureMustReleaseOnceAndReturnUnpublishedWriter()
        => await VerifySnapshotCleanupAsync(serializeAlsoThrows: false);

    [Test]
    public async Task SerializerFailureMustRemainPrimaryWhenSnapshotReleaseAlsoFails()
        => await VerifySnapshotCleanupAsync(serializeAlsoThrows: true);

    private static async Task VerifySnapshotCleanupAsync(bool serializeAlsoThrows)
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "snapshot-ownership-failure",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 16,
            connectionReceiveWindowBytes: 64);
        var controller = GetFlowController(session);
        var serializationFailure = new InvalidOperationException("original serialization failure");
        var releaseFailure = new InvalidOperationException("snapshot release failure");
        var codec = new ThrowingReleaseCodec(
            serializeAlsoThrows ? serializationFailure : null,
            releaseFailure);
        Exception? observed = null;
        try
        {
            try
            {
                await session.SendStreamChunkKnownSizeAsync(
                    71, 1, 7, codec, sizeof(int), codec.Snapshot, CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                observed = exception;
            }

            var writerReturned = IsInactive(codec.Writer);
            var originalPreserved = ReferenceEquals(
                observed, serializeAlsoThrows ? serializationFailure : releaseFailure);
            // Read all outcomes before asserting so the retained red gives ownership and
            // exception observations together; no cleanup operation is called a success merely
            // because an earlier exception prevented its execution.
            Ensure(originalPreserved && codec.ReleaseCalls == 1 && writerReturned,
                $"primaryPreserved={originalPreserved}, releaseCalls={codec.ReleaseCalls}, " +
                $"writerReturned={writerReturned}, observed={observed?.Message}");
            Ensure(controller.SendConnectionCredit == 64,
                "a never-published item must refund its exact connection credit");
            Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
                "known-size failure must leave no pre-credit owner or waiter");
            session.CompleteSendStream(71, 1);
            Ensure(controller.ActiveSendStreamCount == 0 && controller.RetainedSendStreamCount == 0,
                "retirement after the refund must leave no active or retained send state");
        }
        finally
        {
            // If the unchanged implementation leaks this writer, release it only after the
            // outcome has been observed. This is fixture cleanup, not part of the tested path.
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
            throw new InvalidOperationException($"Snapshot ownership assertion failed: {message}.");
    }

    private sealed class SnapshotToken : IRpcSizedCodecSnapshot { }

    private sealed class ThrowingReleaseCodec(Exception? serializationFailure, Exception releaseFailure)
        : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        internal IRpcSizedCodecSnapshot Snapshot { get; } = new SnapshotToken();
        internal IRpcByteBufferWriter? Writer { get; private set; }
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
            snapshot = Snapshot;
            return true;
        }

        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(ReferenceEquals(snapshot, Snapshot), "sized helper must use the original snapshot");
            Writer = (IRpcByteBufferWriter)buffer;
            BitConverter.TryWriteBytes(buffer.GetSpan(sizeof(int)), value);
            buffer.Advance(sizeof(int));
            if (serializationFailure is not null)
                throw serializationFailure;
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(ReferenceEquals(snapshot, Snapshot), "release must receive the captured snapshot");
            ReleaseCalls++;
            throw releaseFailure;
        }

        public void Serialize(in int value, IBufferWriter<byte> buffer)
            => throw new InvalidOperationException("Unexpected unsized serialization.");

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);
    }
}
