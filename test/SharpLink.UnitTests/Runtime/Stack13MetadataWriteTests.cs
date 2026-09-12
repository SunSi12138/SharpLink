using System.Linq;
using System.Text;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class Stack13MetadataWriteTests
{
    [Test]
    public void MetadataWriteShouldCoalescePrefixAndContentWithoutChangingWire()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(index => new KeyValuePair<string, string>("key" + index, "value🙂" + index)).ToArray();
        var metadata = new SharpLinkMetadata(entries);
        var writer = new CountingWriter();
        ProtocolV2PayloadCodec.WriteMetadata(writer, metadata);
        Ensure(writer.SpanCalls <= 17, "one reservation for count and one per key/value");
        var roundTrip = ProtocolV2PayloadCodec.ReadMetadata(new ReadOnlySequence<byte>(writer.Written));
        Ensure(roundTrip.Count == entries.Length, "metadata count");
        for (var index = 0; index < entries.Length; index++)
            Ensure(roundTrip[index].Equals(entries[index]), "metadata wire round trip");
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
