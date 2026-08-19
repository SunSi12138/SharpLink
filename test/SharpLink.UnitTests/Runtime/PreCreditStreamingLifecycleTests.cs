using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditStreamingLifecycleTests
{
    [Test]
    public async Task ByteBudgetWaitCancellationShouldReleasePermitAndCreditWaitCancellationShouldReleaseOwner()
    {
        const int payloadBytes = 1024;
        var codec = new CountingUnsizedCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "pre-credit-cancel",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);

        await session.SendStreamChunkAsync(1, 1, new Payload(payloadBytes));
        Ensure(codec.SerializeCount == 1, "the first item should consume the only flow credit");

        using var creditWaitCancellation = new CancellationTokenSource();
        var creditWaiter = session.SendStreamChunkAsync(
            2,
            1,
            new Payload(payloadBytes),
            creditWaitCancellation.Token).AsTask();
        Ensure(!creditWaiter.IsCompleted, "the second item should wait for flow credit");
        Ensure(codec.SerializeCount == 2, "the flow-credit waiter should own one serialized item");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "the credit waiter should own exactly one serialized payload");
        Ensure(session.PreCreditActiveSerializerCount == 1,
            "the flow-credit waiter should retain one serializer permit");

        using var budgetWaitCancellation = new CancellationTokenSource();
        var budgetWaiter = session.SendStreamChunkAsync(
            3,
            1,
            new Payload(payloadBytes),
            budgetWaitCancellation.Token).AsTask();
        Ensure(!budgetWaiter.IsCompleted, "the third item should wait for actual-byte admission");
        Ensure(codec.SerializeCount == 3,
            "a byte-budget waiter may serialize only while it owns a bounded serializer permit");
        Ensure(session.PreCreditActiveSerializerCount == 2,
            "both materialized blocked writers should retain serializer permits");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "the byte-budget waiter should be represented by one bounded queue node");

        budgetWaitCancellation.Cancel();
        await ExpectCancellation(budgetWaiter);
        Ensure(codec.SerializeCount == 3, "cancelled byte-budget wait must not reserialize discarded data");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "cancelled byte-budget wait must leave no waiter node behind");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "cancelling a follower must not steal the active oversized owner's bytes");
        Ensure(session.PreCreditActiveSerializerCount == 1,
            "cancelling a byte-budget waiter must release its serializer permit exactly once");

        creditWaitCancellation.Cancel();
        await ExpectCancellation(creditWaiter);
        Ensure(session.PreCreditSerializedBytes == 0,
            "credit-wait cancellation must release the serialized byte owner exactly once");
        Ensure(session.PreCreditActiveSerializerCount == 0 && session.PreCreditSerializedWaiterCount == 0,
            "all cancellation cleanup must leave serializer and byte admission empty");
    }

    [Test]
    public async Task StreamTerminalShouldWakeMatchingByteBudgetWaiterWithoutDisturbingOtherStream()
    {
        const int payloadBytes = 1024;
        var codec = new CountingUnsizedCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "pre-credit-stream-terminal",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);

        await session.SendStreamChunkAsync(10, 1, new Payload(payloadBytes));
        var serializedOwner = session.SendStreamChunkAsync(11, 1, new Payload(payloadBytes)).AsTask();
        var matchingBudgetWaiter = session.SendStreamChunkAsync(12, 1, new Payload(payloadBytes)).AsTask();
        var otherBudgetWaiter = session.SendStreamChunkAsync(13, 1, new Payload(payloadBytes)).AsTask();
        Ensure(codec.SerializeCount == 4,
            "the bounded serializer gate should materialize these three blocked test items");
        Ensure(session.PreCreditActiveSerializerCount == 3,
            "all three blocked materialized writers should own serializer permits");
        Ensure(session.PreCreditSerializedWaiterCount == 2,
            "two later streams should wait for the actual-byte budget");

        var streamTerminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream 12 closed");
        session.SendStreamErrorAsync(12, 1, streamTerminal);
        await ExpectSameException(matchingBudgetWaiter, streamTerminal);
        Ensure(!otherBudgetWaiter.IsCompleted,
            "terminating one stream must not reject a different pre-credit waiter");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "only the matching stream waiter should leave the bounded queue");
        Ensure(session.PreCreditActiveSerializerCount == 2,
            "the rejected byte-budget waiter must release exactly one serializer permit");
        Ensure(codec.SerializeCount == 4,
            "stream-terminal rejection must not cause any item to be serialized twice");

        var ownerTerminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream 11 closed");
        session.SendStreamErrorAsync(11, 1, ownerTerminal);
        await ExpectSameException(serializedOwner, ownerTerminal);

        // Releasing the actual-byte owner admits the already-materialized surviving stream,
        // which keeps its serializer permit while it waits for flow credit.
        await SpinUntilAsync(() =>
            session.PreCreditSerializedWaiterCount == 0 &&
            session.PreCreditSerializedBytes == payloadBytes &&
            session.PreCreditActiveSerializerCount == 1);
        Ensure(!otherBudgetWaiter.IsCompleted, "the surviving stream should now be waiting for flow credit");
        Ensure(codec.SerializeCount == 4,
            "admitting an already-materialized byte-budget waiter must not serialize it again");

        var otherTerminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream 13 closed");
        session.SendStreamErrorAsync(13, 1, otherTerminal);
        await ExpectSameException(otherBudgetWaiter, otherTerminal);
        Ensure(
            session.PreCreditSerializedBytes == 0 &&
            session.PreCreditActiveSerializerCount == 0 &&
            session.PreCreditSerializedWaiterCount == 0,
            "stream terminal cleanup must return all pre-credit accounting to zero");
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            if (condition())
                return;
            await Task.Yield();
        }
        throw new InvalidOperationException("The expected pre-credit transition did not occur.");
    }

    private static async Task ExpectCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("The send did not observe cancellation.");
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
        throw new InvalidOperationException("The send did not observe the expected stream terminal exception.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Pre-credit lifecycle assertion failed: {scenario}.");
    }

    private readonly record struct Payload(int Bytes);

    private sealed class CountingUnsizedCodec : IRpcCodec<Payload>
    {
        private int _serializeCount;
        internal int SerializeCount => Volatile.Read(ref _serializeCount);

        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _serializeCount);
            var span = buffer.GetSpan(value.Bytes);
            span[..value.Bytes].Fill(0x7a);
            buffer.Advance(value.Bytes);
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));
    }
}
