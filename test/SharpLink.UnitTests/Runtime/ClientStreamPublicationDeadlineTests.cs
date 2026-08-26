using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public class ClientStreamPublicationDeadlineTests
{
    [Test]
    public async Task UnsizedChunkShouldNotPublishWhenSerializationCrossesDeadline()
        => await AssertSerializationCrossingDeadlineDoesNotPublishAsync(
            static timeProvider => new AdvancingIntCodec(timeProvider),
            "unsized");

    [Test]
    public async Task SizedChunkShouldNotPublishWhenSerializationCrossesDeadline()
        => await AssertSerializationCrossingDeadlineDoesNotPublishAsync(
            static timeProvider => new AdvancingSizedIntCodec(timeProvider),
            "sized");

    [Test]
    public async Task ExpiredCleanAndErrorCompletionShouldNotPublish()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "client-stream-terminal-publication-deadline",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var timeProvider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

        var cleanFailure = CaptureFailure(() => session.SendClientStreamComplete(
            91,
            0,
            deadline,
            timeProvider,
            CancellationToken.None));
        Ensure(cleanFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "clean client-stream EOF must be rejected at its enqueue boundary after expiry");

        var errorFailure = CaptureFailure(() => session.SendClientStreamError(
            92,
            0,
            new SharpLinkException(SharpLinkErrorCode.Internal, "producer"),
            deadline,
            timeProvider,
            CancellationToken.None));
        Ensure(errorFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "error client-stream EOF must be rejected at its enqueue boundary after expiry");

        session.SendRpcErrorAsync(
            999,
            new SharpLinkException(SharpLinkErrorCode.Internal, "marker"));
        var frames = await FlushAndReadFramesAsync(session, output);
        Ensure(frames.Count == 1 && frames[0] == ProtocolV2FrameType.Response,
            "expired client-stream terminals must leave no StreamComplete frame in the send queue");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task AssertSerializationCrossingDeadlineDoesNotPublishAsync(
        Func<ManualTimeProvider, IRpcCodec<int>> codecFactory,
        string path)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            $"client-{path}-publication-deadline",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var timeProvider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var codec = codecFactory(timeProvider);
        Exception? failure = null;

        try
        {
            await session.SendClientStreamChunkAsync(
                90,
                0,
                7,
                codec,
                deadline,
                timeProvider,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            $"the {path} client-stream publication boundary must reject a chunk serialized after the deadline");

        session.SendRpcErrorAsync(
            999,
            new SharpLinkException(SharpLinkErrorCode.Internal, "marker"));
        var frames = await FlushAndReadFramesAsync(session, output);
        Ensure(frames.Count == 1 && frames[0] == ProtocolV2FrameType.Response,
            $"the {path} path must not publish StreamData after its deadline");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task<List<ProtocolV2FrameType>> FlushAndReadFramesAsync(
        RpcSession session,
        Pipe output)
    {
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var remaining = read.Buffer;
        var frames = new List<ProtocolV2FrameType>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref remaining,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out _))
        {
            frames.Add(header.Type);
        }
        Ensure(remaining.IsEmpty,
            "client-stream publication output must contain only complete Protocol v2 frames");
        output.Reader.AdvanceTo(read.Buffer.End);
        return frames;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AdvancingIntCodec(ManualTimeProvider timeProvider) : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(2));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);
    }

    private sealed class AdvancingSizedIntCodec(ManualTimeProvider timeProvider)
        : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        public bool CanExactSize => true;

        public void Serialize(in int value, IBufferWriter<byte> buffer)
            => SerializeCore(value, buffer);

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);

        public bool TryGetEncodedSize(in int value, out int size)
        {
            size = sizeof(int);
            return true;
        }

        public bool TryGetEncodedSize(
            in int value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = sizeof(int);
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => SerializeCore(value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }

        private void SerializeCore(int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(2));
        }
    }
}
