using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditSerializedEnvelopeTests
{
    private const int MiB = 1024 * 1024;

    [Test]
    public async Task MixedSizeDefaultEnvelopeShouldIncludeBoundedSerializedWaiter()
    {
        const int budgetBytes = 4 * MiB;
        const int wireWindowBytes = 4 * MiB;
        const int ownerBytes = 2 * MiB;
        const int waiterBytes = (4 * MiB) - (64 * 1024);

        var builder = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = wireWindowBytes;
                options.FlowControl.ConnectionReceiveWindowBytes = wireWindowBytes;
            })
            .AddCodec(new PayloadCodec());
        using var context = builder.Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "pre-credit-mixed-size-envelope",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: wireWindowBytes,
            connectionReceiveWindowBytes: wireWindowBytes);

        for (var index = 0; index < 4; index++)
        {
            await session.AcquireStreamSendCreditAsync(
                requestId: 900_000,
                streamId: 1,
                encodedBytes: MiB,
                CancellationToken.None);
        }

        var firstOwner = session.SendStreamChunkAsync(1, 1, new Payload(ownerBytes)).AsTask();
        var secondOwner = session.SendStreamChunkAsync(2, 1, new Payload(ownerBytes)).AsTask();
        var waiter = session.SendStreamChunkAsync(3, 1, new Payload(waiterBytes)).AsTask();
        var rejected = session.SendStreamChunkAsync(4, 1, new Payload(1)).AsTask();

        Ensure(!firstOwner.IsCompleted && !secondOwner.IsCompleted && !waiter.IsCompleted,
            "two byte owners plus one already-serialized waiter should remain pending under starvation");
        Ensure(session.PreCreditSerializedByteLimit == budgetBytes,
            "the default owner/admission byte budget should remain 4 MiB");
        Ensure(session.PreCreditSerializedBytes == budgetBytes,
            "the two 2 MiB owners should consume the entire owner/admission byte budget");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "the default 4 MiB budget and 4 MiB max frame should allow exactly one serialized waiter");
        await ExpectResourceExhausted(rejected);

        var maxFrameBytes = context.Options.Protocol.MaxFramePayloadBytes;
        var derivedWaiters = Math.Min(
            context.Options.Protocol.MaxConcurrentStreamsPerConnection,
            Math.Max(1, budgetBytes / maxFrameBytes));
        var aggregatePayloadBound =
            Math.Max((long)budgetBytes, maxFrameBytes) + ((long)derivedWaiters * maxFrameBytes);
        var controlledRetainedPayloadBytes = (2L * ownerBytes) + waiterBytes;

        Ensure(controlledRetainedPayloadBytes > budgetBytes,
            "a serialized waiter is retained outside the owner/admission byte budget");
        Ensure(aggregatePayloadBound == 8L * MiB,
            "the default aggregate serialized-payload envelope should be 8 MiB before frame/pool overhead");
        Ensure(controlledRetainedPayloadBytes <= aggregatePayloadBound,
            "mixed-size owner plus waiter backing must remain within the documented aggregate envelope");

        var terminal = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "mixed-size envelope cleanup");
        session.NotifyDisconnected(terminal);
        await ExpectSameException(firstOwner, terminal);
        await ExpectSameException(secondOwner, terminal);
        await ExpectSameException(waiter, terminal);
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "mixed-size terminal cleanup must return owner and waiter accounting to zero");
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
        throw new InvalidOperationException("Expected excess mixed-size pre-credit admission to fail boundedly.");
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
        throw new InvalidOperationException("The mixed-size pre-credit send did not observe the expected terminal.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Pre-credit envelope assertion failed: {scenario}.");
    }

    private readonly record struct Payload(int Bytes);

    private sealed class PayloadCodec : IRpcCodec<Payload>
    {
        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(value.Bytes);
            span[..value.Bytes].Fill(0x4d);
            buffer.Advance(value.Bytes);
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));
    }
}
