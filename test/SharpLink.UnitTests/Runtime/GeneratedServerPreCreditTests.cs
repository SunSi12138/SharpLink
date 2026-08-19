using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class GeneratedServerPreCreditTests
{
    [Test]
    public async Task ConcurrentUnsizedPumpsShouldShareOneSessionPreCreditEnvelope()
    {
        const int payloadBytes = 1024;
        const int pumpCount = 8;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-pre-credit-bound",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);

        await session.AcquireStreamSendCreditAsync(900, 1, 1, CancellationToken.None);
        var codec = new CountingUnsizedCodec();
        var bridge = new RpcSessionGeneratedServerBridge(session);
        var pumps = new Task[pumpCount];
        for (var index = 0; index < pumps.Length; index++)
        {
            pumps[index] = bridge.PumpOutboundStreamAsync(
                requestId: index + 1,
                streamId: 1,
                new SingleItemAsyncEnumerable<Payload>(new Payload(payloadBytes)),
                codec,
                payloadNullable: false,
                contractId: 100,
                methodId: 200,
                CancellationToken.None).AsTask();
        }

        Ensure(codec.SerializeCount == pumpCount,
            "generated unsized items should serialize exactly once before actual-byte admission");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "the generated server should own exactly one oversized actual-byte reservation");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "a one-byte budget should retain only one additional serialized generated waiter");
        Ensure(session.PreCreditSerializationPermitLimit == 0,
            "generated streaming must not use a global serializer gate");

        var pending = new List<Task>();
        var rejected = 0;
        for (var index = 0; index < pumps.Length; index++)
        {
            if (!pumps[index].IsCompleted)
            {
                pending.Add(pumps[index]);
                continue;
            }
            await ExpectResourceExhausted(pumps[index]);
            rejected++;
        }
        Ensure(pending.Count == 2, "only the byte owner and one serialized waiter should remain pending");
        Ensure(rejected == pumpCount - pending.Count,
            "excess generated pumps should fail instead of retaining unbounded writers");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "generated cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < pending.Count; index++)
            await ExpectSameException(pending[index], terminal);

        Ensure(
            session.PreCreditSerializedBytes == 0 &&
            session.PreCreditSerializedWaiterCount == 0,
            "generated-server terminal cleanup must release all pre-credit ownership");
    }

    [Test]
    public async Task SharedInvocationCancellationShouldReleaseGeneratedPreCreditOwnerAndWaiter()
    {
        const int payloadBytes = 1024;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-pre-credit-request-cancel",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);

        await session.AcquireStreamSendCreditAsync(900, 1, 1, CancellationToken.None);
        var codec = new CountingUnsizedCodec();
        var bridge = new RpcSessionGeneratedServerBridge(session);
        using var invocationCancellation = new CancellationTokenSource();

        var owner = bridge.PumpOutboundStreamAsync(
            requestId: 50,
            streamId: 1,
            new SingleItemAsyncEnumerable<Payload>(new Payload(payloadBytes)),
            codec,
            payloadNullable: false,
            contractId: 100,
            methodId: 200,
            invocationCancellation.Token).AsTask();
        var waiter = bridge.PumpOutboundStreamAsync(
            requestId: 50,
            streamId: 2,
            new SingleItemAsyncEnumerable<Payload>(new Payload(payloadBytes)),
            codec,
            payloadNullable: false,
            contractId: 100,
            methodId: 200,
            invocationCancellation.Token).AsTask();

        Ensure(codec.SerializeCount == 2,
            "both generated outbound streams should serialize once before cancellation");
        Ensure(!owner.IsCompleted && !waiter.IsCompleted,
            "one generated send should wait for flow credit while the sibling waits for byte admission");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "the invocation should have one actual-byte owner before cancellation");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "the invocation should have one bounded serialized waiter before cancellation");

        invocationCancellation.Cancel();
        await ExpectCancellation(owner);
        await ExpectCancellation(waiter);

        Ensure(session.PreCreditSerializedBytes == 0,
            "invocation token cancellation must release the flow-credit owner's byte reservation");
        Ensure(session.PreCreditSerializedWaiterCount == 0,
            "invocation token cancellation must remove the sibling budget waiter");
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
        throw new InvalidOperationException("Generated excess pump did not fail with ResourceExhausted.");
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
        throw new InvalidOperationException("Generated server pump did not observe invocation cancellation.");
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
        throw new InvalidOperationException("Generated server pump did not observe the session terminal exception.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Generated pre-credit assertion failed: {scenario}.");
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
            span[..value.Bytes].Fill(0x42);
            buffer.Advance(value.Bytes);
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));
    }

    private sealed class SingleItemAsyncEnumerable<T>(T item) : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private bool _moved;
        public T Current { get; private set; } = default!;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_moved)
                return ValueTask.FromResult(false);
            _moved = true;
            Current = item;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
