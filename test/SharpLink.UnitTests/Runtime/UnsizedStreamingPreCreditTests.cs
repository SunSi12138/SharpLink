using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class UnsizedStreamingPreCreditTests
{
    [Test]
    public async Task CreditStarvationShouldBoundLongLivedSerializedOwnersAndWaiters()
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
            "unsized-pre-credit-bound",
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
        }

        // Unknown-size codecs must serialize once to discover actual bytes. Once credit is known to
        // be unavailable, however, only one oversized byte-budget owner and one bounded budget
        // waiter may remain live with this 1-byte connection window.
        Ensure(codec.SerializeCount == 1 + blockedStreams,
            "each unsized item should serialize exactly once before actual-byte admission");
        Ensure(session.PreCreditSerializedByteLimit == 1,
            "the actual-byte budget should derive from the negotiated connection window");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "only one oversized item may own the one-byte actual serialized-byte budget");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "a sub-frame connection window should retain at most one serialized budget waiter");
        Ensure(session.PreCreditSerializationPermitLimit == 0 && session.PreCreditActiveSerializerCount == 0,
            "the final design must not use a global pre-serialization permit gate");

        var pendingCount = 0;
        var rejectedCount = 0;
        for (var index = 0; index < blocked.Length; index++)
        {
            if (!blocked[index].IsCompleted)
            {
                pendingCount++;
                continue;
            }

            await ExpectResourceExhausted(blocked[index]);
            rejectedCount++;
        }
        Ensure(pendingCount == 2,
            "only the byte owner and one bounded serialized waiter should remain pending");
        Ensure(rejectedCount == blockedStreams - pendingCount,
            "excess serialized producers should fail instead of growing the retained-writer queue");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "bounded cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < blocked.Length; index++)
        {
            if (!blocked[index].IsCompletedSuccessfully && blocked[index].Exception is null)
                await ExpectSameException(blocked[index], terminal);
        }

        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "terminal cleanup must release every pre-credit byte owner and waiter");
    }

    [Test]
    public async Task ExactSizeCodecShouldBypassPreCreditSerializedBudget()
    {
        const int payloadBytes = 1024;
        var codec = new SizedPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "sized-pre-credit-bypass",
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
            requestId: 11,
            streamId: 1,
            new SizedPayload(payloadBytes));
        Ensure(codec.SerializeCount == 1, "the first exact-size item should serialize after acquiring credit");

        var blocked = session.SendStreamChunkAsync(
            requestId: 12,
            streamId: 1,
            new SizedPayload(payloadBytes)).AsTask();
        Ensure(!blocked.IsCompleted, "the second exact-size item should wait for flow credit");
        Ensure(codec.SerializeCount == 1,
            "the exact-size item must not serialize while flow credit is exhausted");
        Ensure(session.PreCreditSerializedByteLimit == 0,
            "exact-size streaming must not instantiate the unsized pre-credit byte budget");
        Ensure(session.PreCreditSerializationPermitLimit == 0,
            "exact-size streaming must not instantiate a pre-serialization gate");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "sized cleanup");
        session.NotifyDisconnected(terminal);
        await ExpectSameException(blocked, terminal);
    }

    private static async Task ExpectResourceExhausted(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
            return;
        }
        throw new InvalidOperationException("The excess pre-credit send did not fail with ResourceExhausted.");
    }

    private static async Task ExpectSameException(Task task, Exception expected)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
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
            throw new InvalidOperationException($"Pre-credit streaming assertion failed: {scenario}.");
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

    private readonly record struct SizedPayload(int Bytes);

    private sealed class SizedPayloadCodec : IRpcCodec<SizedPayload>, IRpcSizedCodec<SizedPayload>
    {
        private int _serializeCount;

        internal int SerializeCount => Volatile.Read(ref _serializeCount);
        public bool CanExactSize => true;

        public void Serialize(in SizedPayload value, IBufferWriter<byte> buffer)
            => SerializeCore(value, buffer);

        public SizedPayload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));

        public bool TryGetEncodedSize(in SizedPayload value, out int size)
        {
            size = value.Bytes;
            return true;
        }

        public bool TryGetEncodedSize(
            in SizedPayload value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = value.Bytes;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in SizedPayload value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(size == value.Bytes, "sized codec received an unexpected encoded size");
            Ensure(snapshot is null, "test sized codec should not receive a snapshot");
            SerializeCore(value, buffer);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(snapshot is null, "test sized codec should not release a non-null snapshot");
        }

        private void SerializeCore(in SizedPayload value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _serializeCount);
            var span = buffer.GetSpan(value.Bytes);
            span[..value.Bytes].Fill(0x33);
            buffer.Advance(value.Bytes);
        }
    }
}
