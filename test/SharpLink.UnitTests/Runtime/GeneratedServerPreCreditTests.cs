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

        // Exhaust the one negotiated send-credit byte without using the test codec.
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
            Ensure(!pumps[index].IsCompleted, "credit-starved generated pumps should remain blocked");
        }

        Ensure(codec.SerializeCount == 1,
            "only one generated-server item may serialize while the one-byte budget is oversize-borrowed");
        Ensure(session.PreCreditSerializedBytes == payloadBytes,
            "the generated server should own exactly one oversized serialized item");
        Ensure(session.PreCreditSerializedWaiterCount == pumpCount - 1,
            "all remaining generated pumps should wait before serialization");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "generated cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < pumps.Length; index++)
            await ExpectSameException(pumps[index], terminal);

        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "generated-server terminal cleanup must release all pre-credit ownership");
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
