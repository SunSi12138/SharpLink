using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
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
            .Configure(options => options.FlowControl.MaxPreCreditSerializedBytes = payloadBytes)
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
            "cancelling a follower must not steal the active byte owner");

        creditWaitCancellation.Cancel();
        await ExpectCancellation(creditWaiter);
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "credit-wait cancellation must release the serialized byte owner exactly once");
    }

    [Test]
    public async Task UnsizedSendPacketFailureAfterPreCreditHandoffShouldRefundCreditExactlyOnce()
    {
        const long requestId = 20;
        const ushort streamId = 3;
        const int payloadBytes = 8;
        const int streamWindow = payloadBytes;
        const int connectionWindow = payloadBytes;
        var codec = new CountingUnsizedCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.MaxPreCreditSerializedBytes = payloadBytes;
                options.FlowControl.MaxSendQueueBytes = 1;
            })
            .AddCodec(codec)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "pre-credit-send-failure",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: streamWindow,
            connectionReceiveWindowBytes: connectionWindow);
        var controller = GetFlowController(session);
        var preSendConnectionCredit = controller.SendConnectionCredit;

        Ensure(
            controller.TryAcquireSendCredit(requestId, streamId, payloadBytes),
            "test setup should exhaust the stream and connection send windows");
        Ensure(controller.SendConnectionCredit == 0,
            "test setup should leave no connection credit before the unsized send");

        var pending = session.SendStreamChunkAsync(
            requestId,
            streamId,
            new Payload(payloadBytes)).AsTask();
        Ensure(!pending.IsCompleted,
            "the unsized sender should wait for flow credit after serialized-byte admission");
        Ensure(codec.SerializeCount == 1,
            "the unsized slow path should serialize exactly once before waiting for credit");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "the blocked unsized sender should own the serialized-byte budget");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "the blocked sender should be the admitted byte owner, not a budget waiter");

        session.ApplyWindowUpdate(
            requestId,
            new ProtocolV2WindowUpdate(streamId, payloadBytes));

        SharpLinkException? sendFailure = null;
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (SharpLinkException exception) when (
            exception.Code == SharpLinkErrorCode.ResourceExhausted &&
            exception.Message.Contains("send_queue_capacity", StringComparison.Ordinal))
        {
            sendFailure = exception;
        }
        Ensure(sendFailure is not null,
            "SendPacket should fail deterministically after flow-credit ownership transfers");
        Ensure(session.PreCreditSerializedBytes == 0,
            "SendPacket failure must leave no serialized-byte owner behind");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "SendPacket failure must leave no serialized-byte waiter behind");
        Ensure(controller.SendConnectionCredit == preSendConnectionCredit,
            "SendPacket failure must restore connection send credit to its pre-send value");
        Ensure(GetSendStreamCredit(controller, requestId, streamId) == streamWindow,
            "SendPacket failure must restore stream send credit to its pre-send value");

        try
        {
            session.ReturnUnsentStreamCredit(requestId, streamId, payloadBytes);
            throw new InvalidOperationException("expected double refund failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("returned more than once", StringComparison.Ordinal))
        {
        }

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task StreamTerminalShouldWakeMatchingBudgetWaiterAndOwner()
    {
        const int payloadBytes = 1024;
        var codec = new CountingUnsizedCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxPreCreditSerializedBytes = payloadBytes)
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
            "one actual-byte owner should remain while credit is exhausted");
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

    private static StreamFlowController GetFlowController(RpcSession session)
    {
        var field = typeof(RpcSession).GetField(
            "_protocolState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RpcSession._protocolState field was not found.");
        var state = (RpcSessionProtocolState)field.GetValue(session)!;
        return state.FlowController
            ?? throw new InvalidOperationException("The test session did not negotiate flow control.");
    }

    private static long GetSendStreamCredit(
        StreamFlowController controller,
        long requestId,
        ushort streamId)
    {
        var statesField = typeof(StreamFlowController).GetField(
            "_sendStates",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("StreamFlowController._sendStates field was not found.");
        var states = (System.Collections.IEnumerable)statesField.GetValue(controller)!;
        foreach (var entry in states)
        {
            var entryType = entry!.GetType();
            var key = entryType.GetProperty("Key")!.GetValue(entry)!;
            var value = entryType.GetProperty("Value")!.GetValue(entry)!;
            var keyType = key.GetType();
            var entryRequestId = (long)keyType.GetProperty(
                "RequestId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(key)!;
            var entryStreamId = (ushort)keyType.GetProperty(
                "StreamId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(key)!;
            if (entryRequestId != requestId || entryStreamId != streamId)
                continue;

            var creditField = value.GetType().GetField(
                "Credit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("StreamFlowController.SendState.Credit field was not found.");
            return (long)creditField.GetValue(value)!;
        }

        throw new InvalidOperationException("The expected send-stream state was not found.");
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
