using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("nullability-guard-pool")]
public sealed class StreamNullabilityGuardTests
{
    private static readonly ReadOnlySequence<byte> Payload = new(new byte[] { 1 });

    [Test]
    public async Task AlternatingNullableRentalsMustPreserveNullFailureAndReturnCreditOnce()
    {
        PooledAsyncStreamDispatcher<NullItem>.ClearPoolForTests();
        PooledAsyncStreamDispatcher<NullItem>? previous = null;
        foreach (var nullable in new[] { true, false, true, false })
        {
            var dispatcher = PooledAsyncStreamDispatcher<NullItem>.Rent(default, new NullCodec(), nullable);
            if (previous is not null)
                Ensure(ReferenceEquals(previous, dispatcher), "the same pooled object must exercise both contracts");
            var calls = 0;
            var bytes = 0;
            dispatcher.SetBytesConsumedCallback((requestId, streamId, count) =>
            {
                Ensure(requestId == 73 && streamId == 1, "credit must retain its owner");
                calls++;
                bytes += count;
            }, 73, 1);
            Exception? failure = null;
            try { await dispatcher.DispatchAsync(Payload, 17); }
            catch (Exception exception) { failure = exception; }
            if (nullable)
                Ensure(failure is null && calls == 0, "nullable null is buffered before credit is consumed");
            else
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss, Message: "A non-nullable RPC stream item was null." }, "required custom-codec null must preserve its exact error");
            dispatcher.Complete(null);
            var enumerator = dispatcher.GetAsyncEnumerator();
            if (nullable)
                Ensure(await enumerator.MoveNextAsync() && enumerator.Current is null, "nullable custom-codec null must survive");
            Ensure(!await enumerator.MoveNextAsync(), "rejected null must not be queued");
            await enumerator.DisposeAsync();
            Ensure(calls == 1 && bytes == 17, "success or null failure must return encoded credit exactly once");
            previous = dispatcher;
        }
        PooledAsyncStreamDispatcher<NullItem>.ClearPoolForTests();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NullableValueTypeMustKeepContractValidation(bool nullable)
    {
        var dispatcher = PooledAsyncStreamDispatcher<int?>.Rent(default, new NullIntCodec(), nullable);
        Exception? failure = null;
        try { await dispatcher.DispatchAsync(Payload); }
        catch (Exception exception) { failure = exception; }
        Ensure(nullable ? failure is null : failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss }, "Nullable<int> is nullable data, not an always-present value type");
        dispatcher.Complete(null);
        var enumerator = dispatcher.GetAsyncEnumerator();
        if (nullable)
            Ensure(await enumerator.MoveNextAsync() && enumerator.Current is null, "Nullable<int> null must survive allowed contracts");
        Ensure(!await enumerator.MoveNextAsync(), "unexpected extra value");
        await enumerator.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerPumpMustValidateBeforeSerializingAndDisposeExactlyOnce(bool nullable)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "nullability-pump", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var codec = new NullCodec();
        var items = new TrackingItems();
        Exception? failure = null;
        try
        {
            await session.PumpGeneratedOutboundStreamAsync(73, 0, items, codec, nullable, CancellationToken.None);
        }
        catch (Exception exception) { failure = exception; }
        Ensure(items.Disposals == 1, "pump must dispose the enumerator once on both paths");
        Ensure(nullable ? failure is null : failure is SharpLinkException { Code: SharpLinkErrorCode.Internal, Message: "A non-nullable RPC stream response was null." }, "pump must retain required-null error semantics");
        Ensure(codec.Serialized == (nullable ? 2 : 1), "rejected null must never reach custom Serialize");
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var remaining = read.Buffer;
        var data = 0;
        var complete = 0;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, session.RuntimeContext.Protocol, out var header, out _))
        {
            Ensure(header.RequestId == 73, "frame must keep request id");
            if (header.Type == ProtocolV2FrameType.StreamData) data++;
            if (header.Type == ProtocolV2FrameType.StreamComplete) complete++;
        }
        Ensure(remaining.IsEmpty && data == (nullable ? 2 : 1) && complete == (nullable ? 1 : 0), "a rejected null must publish neither a partial data frame nor a success terminal");
        output.Reader.AdvanceTo(read.Buffer.End);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private sealed class NullItem;
    private sealed class NullCodec : IRpcCodec<NullItem>
    {
        internal int Serialized;
        public NullItem Deserialize(in ReadOnlySequence<byte> buffer) => null!;
        public void Serialize(in NullItem value, IBufferWriter<byte> buffer)
        {
            Serialized++;
            buffer.GetSpan(1)[0] = value is null ? (byte)0 : (byte)1;
            buffer.Advance(1);
        }
    }
    private sealed class NullIntCodec : IRpcCodec<int?>
    {
        public int? Deserialize(in ReadOnlySequence<byte> buffer) => null;
        public void Serialize(in int? value, IBufferWriter<byte> buffer) => throw new NotSupportedException();
    }
    private sealed class TrackingItems : IAsyncEnumerable<NullItem>, IAsyncEnumerator<NullItem>
    {
        private int _index;
        internal int Disposals;
        public NullItem Current => _index == 1 ? new() : null!;
        public IAsyncEnumerator<NullItem> GetAsyncEnumerator(CancellationToken token = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(++_index <= 2);
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
