using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Client;

public class RpcClientStreamSinkTests
{
    [Test]
    public async Task BoundCodecShouldBeRequiredBySinkContract()
    {
        var codec = new IntCodec();
        var sink = new BoundOnlySink(codec);

        await sink.SendClientStreamAsync(1, 1, Empty(), codec);

        if (!sink.ReceivedExpectedCodec)
            throw new Exception("the sink must receive the construction-time-bound codec");
    }

    private static async IAsyncEnumerable<int> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class BoundOnlySink(object expectedCodec) : IRpcClientStreamSink
    {
        public bool ReceivedExpectedCodec { get; private set; }

        public Task SendClientStreamAsync<T>(
            long requestId,
            ushort streamId,
            IAsyncEnumerable<T> stream,
            IRpcCodec<T> codec,
            CancellationToken cancellationToken = default)
        {
            ReceivedExpectedCodec = ReferenceEquals(codec, expectedCodec);
            return Task.CompletedTask;
        }
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) { }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
