using System.Linq;
using System.Text;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class Stack13Utf8ValidationTests
{
    [Test]
    public void ContiguousStrictUtf8ValidationShouldNotAllocateDecoder()
    {
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteError(writer, SharpLinkErrorCode.Internal, "error🙂中文", 256, out _);
        var payload = new ReadOnlySequence<byte>(writer.WrittenMemory);
        for (var index = 0; index < 100; index++)
            ProtocolV2PayloadCodec.ValidateErrorPayload(payload, 256);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 2000; index++)
            ProtocolV2PayloadCodec.ValidateErrorPayload(payload, 256);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(allocated < 2048, "contiguous valid input must not allocate one decoder per frame");
    }

    private static ISharpLinkClient CreateClient(TimeProvider clock)
        => SharpClientBuilder.Create().UseTransport(new UnusedTransport())
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTimeProvider(clock).DisableRequestTimeout().Build();

    private static void Ensure(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private sealed class CountingClock : TimeProvider
    {
        internal long Now = 1000000;
        internal int Reads;
        public override long TimestampFrequency => 1000000000;
        public override long GetTimestamp()
        {
            Reads++;
            return Now;
        }
    }

    private sealed class UnusedTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new InvalidOperationException("transport must not start"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _writer = new(4096);
        internal int SpanCalls;
        internal ReadOnlyMemory<byte> Written => _writer.WrittenMemory;
        public void Advance(int count) => _writer.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _writer.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SpanCalls++;
            return _writer.GetSpan(sizeHint);
        }
    }
}
