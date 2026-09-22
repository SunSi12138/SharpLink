using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public class StaticCodecCoreRuntimeTests
{
    [Test]
    public async Task GeneratedRequestOperationShouldDecodeAndReuseClosedGenericPool()
    {
        using var table = PendingRequestTableTestFixture.Create(8);
        var codec = new StaticInt32Core();

        var first = table.RentGenerated(
            in codec,
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out var firstId);
        var payload = Payload(41);
        Ensure(table.Dispatch(firstId, ref payload), "generated response should dispatch");
        Ensure(await first.AsValueTask() == 41, "generated response should decode through the static Core");

        var second = table.RentGenerated(
            in codec,
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out var secondId);
        Ensure(ReferenceEquals(first, second),
            "the closed <T,TCore> operation must return to and reuse its bounded pool");

        payload = Payload(42);
        Ensure(table.Dispatch(secondId, ref payload), "reused generated response should dispatch");
        Ensure(await second.AsValueTask() == 42, "reused generated operation must reset response state");
    }

    [Test]
    public async Task GeneratedRequestOperationShouldPreservePayloadlessValidation()
    {
        using var table = PendingRequestTableTestFixture.Create(8);
        var codec = new StaticInt32Core();
        var operation = table.RentGenerated(
            in codec,
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: false);
        var payload = Payload(7);

        Ensure(table.Dispatch(requestId, ref payload), "payload-less generated response should reach its pending operation");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "static Core operation must reject bytes on a payload-less response");
    }

    [Test]
    public async Task GeneratedStreamDispatcherShouldDecodeAndReuseClosedGenericPool()
    {
        PooledAsyncStreamDispatcher<int, StaticInt32Core>.ClearPoolForTests();
        var codec = new StaticInt32Core();
        var first = PooledAsyncStreamDispatcher<int, StaticInt32Core>.Rent(
            default,
            in codec,
            payloadNullable: false);

        await first.DispatchAsync(Payload(17));
        first.Complete(exception: null);
        var enumerator = first.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 17,
            "generated stream dispatcher should decode through its static Core");
        Ensure(!await enumerator.MoveNextAsync(), "completed generated stream should terminate");
        await enumerator.DisposeAsync();

        var second = PooledAsyncStreamDispatcher<int, StaticInt32Core>.Rent(
            default,
            in codec,
            payloadNullable: false);
        Ensure(ReferenceEquals(first, second),
            "the closed <T,TCore> dispatcher must return to and reuse its bounded pool");
        second.Complete(exception: null);
        await second.DisposeAsync();
        PooledAsyncStreamDispatcher<int, StaticInt32Core>.ClearPoolForTests();
    }

    private static ReadOnlySequence<byte> Payload(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return new ReadOnlySequence<byte>(bytes);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
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

    private readonly struct StaticInt32Core : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            if (buffer.Length != sizeof(int))
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "invalid Int32 payload");

            Span<byte> bytes = stackalloc byte[sizeof(int)];
            buffer.CopyTo(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
    }
}
