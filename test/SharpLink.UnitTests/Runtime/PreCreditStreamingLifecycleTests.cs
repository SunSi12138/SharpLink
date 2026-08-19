using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditStreamingLifecycleTests
{
    [Test]
    public async Task BudgetWaitCancellationAndCreditWaitCancellationShouldReleaseOwnership()
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

        using var budgetWaitCancellation = new CancellationTokenSource();
        var budgetWaiter = session.SendStreamChunkAsync(
            3,
            1,
            new Payload(payloadBytes),
            budgetWaitCancellation.Token).AsTask();
        Ensure(!budgetWaiter.IsCompleted, "the third item should wait for actual-byte admission");
        Ensure(codec.SerializeCount == 3,
            "the budget waiter should serialize exactly once before actual-byte admission");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "the byte-budget waiter should be represented by one bounded queue node");

        budgetWaitCancellation.Cancel();
        await ExpectCancellation(budgetWaiter);
        Ensure(codec.SerializeCount == 3, "cancelled budget wait must not reserialize discarded data");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "cancelled budget wait must leave no waiter node behind");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "cancelling a follower must not steal the active oversized owner's bytes");

        creditWaitCancellation.Cancel();
        await ExpectCancellation(creditWaiter);
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "credit-wait cancellation must release the serialized byte owner exactly once");
    }

    [Test]
    public async Task StreamTerminalShouldWakeMatchingBudgetWaiterAndOwner()
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
        Ensure(codec.SerializeCount == 3,
            "the owner and queued follower should each serialize exactly once");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "one oversized actual-byte owner should remain while credit is exhausted");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "one matching serialized waiter should remain queued");

        var waiterTerminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream 12 closed");
        session.SendStreamErrorAsync(12, 1, waiterTerminal);
        await ExpectSameException(matchingBudgetWaiter, waiterTerminal, "matching byte-budget waiter");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "the matching stream terminal should remove its budget waiter");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "terminating the follower must not steal the current byte owner");

        var ownerTerminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream 11 closed");
        session.SendStreamErrorAsync(11, 1, ownerTerminal);
        await ExpectSameException(serializedOwner, ownerTerminal, "current actual-byte owner");
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "stream terminal cleanup must return all pre-credit accounting to zero");
    }

    private static async Task ExpectCancellation(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("The send did not observe cancellation.");
    }

    private static async Task ExpectSameException(Task task, Exception expected, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"The {scenario} did not complete after its terminal transition.",
                exception);
        }
        throw new InvalidOperationException($"The {scenario} did not observe the expected terminal exception.");
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
