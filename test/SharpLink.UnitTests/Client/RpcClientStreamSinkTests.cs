using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Client;

public class RpcClientStreamSinkTests
{
    [Test]
    public async Task BoundCodecOverloadShouldFailWhenSinkDoesNotHonorIt()
    {
        IRpcClientStreamSink sink = new LegacyOnlySink();
        try
        {
            await sink.SendClientStreamAsync(1, 1, Empty(), new IntCodec());
        }
        catch (NotSupportedException)
        {
            return;
        }

        throw new Exception("Expected the default bound-codec overload to fail explicitly.");
    }

    private static async IAsyncEnumerable<int> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class LegacyOnlySink : IRpcClientStreamSink
    {
        public Task SendClientStreamAsync<T>(
  long requestId,
  ushort streamId,
  IAsyncEnumerable<T> stream,
  CancellationToken cancellationToken = default)
  => Task.CompletedTask;
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) { }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
