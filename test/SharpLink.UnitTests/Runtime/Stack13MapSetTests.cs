using System.Linq;
using System.Text;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class Stack13MapSetTests
{
    [Test]
    public async Task MapSingleLookupMustPreserveIdentityAndCountUnderConcurrency()
    {
        var map = new StripedLongMap<object>();
        map.EnableCountTracking();
        var before = new object();
        var after = new object();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 25000; index++)
            {
                var key = ((long)worker << 40) | (uint)index;
                map.Set(key, before);
                map.Set(key, after);
                Ensure(!map.TryRemove(key, before), "identity mismatch must not remove replacement");
                Ensure(map.TryRemove(key, out var value) && ReferenceEquals(value, after), "replacement removed exactly once");
            }
        })));
        Ensure(map.Count == 0, "all stripes drained");
        map.Set(1, null!);
        Ensure(map.Count == 1, "existing null entries still count as entries");
        map.Set(1, after);
        Ensure(map.Count == 1 && map.TryRemove(1, after) && map.Count == 0, "null replacement count");
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
