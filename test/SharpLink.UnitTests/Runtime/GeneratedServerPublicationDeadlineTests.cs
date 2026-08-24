using System.Buffers;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public class GeneratedServerPublicationDeadlineTests
{
    [Test]
    public async Task UnsizedChunkShouldBeRejectedWhenSerializationCrossesDeadline()
        => await AssertSerializationCrossingDeadlineDoesNotPublishAsync(
            new AdvancingIntCodec,
            "unsized");

    [Test]
    public async Task SizedChunkShouldBeRejectedWhenSerializationCrossesDeadline()
        => await AssertSerializationCrossingDeadlineDoesNotPublishAsync(
            new AdvancingSizedIntCodec,
            "sized");

    private static async Task AssertSerializationCrossingDeadlineDoesNotPublishAsync(
        Func<ManualTimeProvider, IRpcCodec<int>> codecFactory,
        string path)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            $"generated-{path}-publication-deadline",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var timeProvider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var codec = codecFactory(timeProvider);
        Exception? failure = null;

        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                   session.SessionId,
                   authentication: null,
                   deadline,
                   timeProvider)))
        {
            try
            {
                await new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                    73,
                    0,
                    SingleValue(7),
                    codec,
                    payloadNullable: false,
                    contractId: 101,
                    methodId: 202,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            $"the {path} publication boundary must reject a chunk serialized after the deadline");

        session.SendStreamErrorAsync(
            73,
            0,
            new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "deadline"));
        var frames = await FlushAndReadFramesAsync(session, output, expectedRequestId: 73);
        Ensure(frames.Count == 1,
            $"the {path} path must not publish StreamData after its deadline");
        Ensure(frames[0].Type == ProtocolV2FrameType.StreamComplete &&
               (frames[0].Flags & ProtocolV2FrameFlags.Error) != 0,
            $"the {path} path must leave only the owner-emitted error terminal");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task<List<(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags)>>
        FlushAndReadFramesAsync(RpcSession session, Pipe output, ulong expectedRequestId)
    {
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var remaining = read.Buffer;
        var frames = new List<(ProtocolV2FrameType, ProtocolV2FrameFlags)>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref remaining,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out _))
        {
            Ensure(header.RequestId == expectedRequestId,
                "every generated publication frame must retain the request ID");
            frames.Add((header.Type, header.Flags));
        }
        Ensure(remaining.IsEmpty,
            "generated publication output must contain only complete Protocol v2 frames");
        output.Reader.AdvanceTo(read.Buffer.End);
        return frames;
    }

    private static async IAsyncEnumerable<int> SingleValue(int value)
    {
        yield return value;
        await Task.Yield();
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
