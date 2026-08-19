using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class UnsizedStreamingPreCreditTests
{
    [Test]
    public async Task CreditStarvationShouldExposeSerializeFirstFallbackAmplification()
    {
        const int payloadBytes = 1024;
        const int blockedStreams = 8;
        var codec = new UnsizedPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "unsized-pre-credit-baseline",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);

        await session.SendStreamChunkAsync(
            requestId: 1,
            streamId: 1,
            new UnsizedPayload(payloadBytes));
        Ensure(codec.SerializeCount == 1, "the first item should consume the only send credit");

        var blocked = new Task[blockedStreams];
        for (var index = 0; index < blocked.Length; index++)
        {
            blocked[index] = session.SendStreamChunkAsync(
                requestId: index + 2,
                streamId: 1,
                new UnsizedPayload(payloadBytes)).AsTask();
            Ensure(!blocked[index].IsCompleted,
                "credit-starved unsized sends should be waiting for WindowUpdate");
        }

        Ensure(codec.SerializeCount == blockedStreams + 1,
            "the serialize-first fallback currently materializes every blocked stream before credit");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "baseline cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < blocked.Length; index++)
            await ExpectSameException(blocked[index], terminal);
    }

    private static async Task ExpectSameException(Task task, Exception expected)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }
        throw new InvalidOperationException("The blocked send did not observe the session terminal exception.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Unsized pre-credit assertion failed: {scenario}.");
    }

    private readonly record struct UnsizedPayload(int Bytes);

    private sealed class UnsizedPayloadCodec : IRpcCodec<UnsizedPayload>
    {
        private int _serializeCount;

        internal int SerializeCount => Volatile.Read(ref _serializeCount);

        public void Serialize(in UnsizedPayload value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _serializeCount);
            var span = buffer.GetSpan(value.Bytes);
            span[..value.Bytes].Fill(0x5a);
            buffer.Advance(value.Bytes);
        }

        public UnsizedPayload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));
    }
}
