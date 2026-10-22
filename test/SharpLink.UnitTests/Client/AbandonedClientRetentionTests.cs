using System.Runtime.CompilerServices;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class AbandonedClientRetentionTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task StreamDoesNotRetainRequestCodecOrReplacedInterceptor(int stage)
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var probe = await CompleteProbe(client, stage);
        ForceCollection();
        Ensure(!probe.Request.IsAlive, "completed stream retained request");
        Ensure(!probe.Codec.IsAlive, "completed stream retained request codec");
        Ensure(!probe.Interceptor.IsAlive, "completed stream retained replaced interceptor generation");
        GC.KeepAlive(probe.Stream);
        GC.KeepAlive(probe.Enumerator);
        await ((IAsyncEnumerator<int>)probe.Enumerator).DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Probe> CompleteProbe(SharpLinkClient client, int stage)
    {
        var request = new Payload();
        var codec = new PayloadCodec();
        var interceptor = new StreamInterceptor();
        client.ReplaceInterceptors([interceptor]);
        var channel = (IRpcChannel)client;
        var method = new RpcMethodDescriptor(1, 929, RpcMethodKind.ServerStreaming, true, false, false, null);
        var stream = channel.InvokeServerStreamingAsync(method, request, codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(), null);
        var enumerator = stream.GetAsyncEnumerator();
        if (stage != 1)
            Ensure(await enumerator.MoveNextAsync(), "first local item");
        if (stage == 0)
        {
            Ensure(!await enumerator.MoveNextAsync(), "local stream completion");
            await enumerator.DisposeAsync();
        }
        client.ReplaceInterceptors([]);
        return new Probe(stream, enumerator, new WeakReference(request), new WeakReference(codec), new WeakReference(interceptor));
    }

    private static void ForceCollection()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record Probe(object Stream, object Enumerator, WeakReference Request, WeakReference Codec, WeakReference Interceptor);
    private sealed class Payload;

    private sealed class PayloadCodec : IRpcCodec<Payload>
    {
        public void Serialize(in Payload value, IBufferWriter<byte> writer) => throw new InvalidOperationException("short circuit");
        public Payload Deserialize(in ReadOnlySequence<byte> payload) => throw new NotSupportedException();
    }

    private sealed class StreamInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(new EmptyStream()));
    }

    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private bool _moved;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { var hasNext = !_moved; _moved = true; return ValueTask.FromResult(hasNext); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
