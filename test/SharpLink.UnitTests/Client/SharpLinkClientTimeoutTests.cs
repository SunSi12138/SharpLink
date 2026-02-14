using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientTimeoutTests
{
    [Test]
    public async Task InvokeWithTimeoutNoPayloadAsyncShouldTimeoutAndSendCancel()
    {
        var transport = new FakeTransport();
        var serializer = new NoopSerializer();
        using var client = new SharpLinkClient(
            transport,
            serializer,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        Ensure(await client.ConnectAsync(), "connect");

        var invokeTask = client.InvokeWithTimeoutNoPayloadAsync<int>(1, 2, TimeSpan.FromMilliseconds(80)).AsTask();
        var callPacket = await transport.Session.WaitForSentPacket(PacketType.RpcCall);
        await EnsureThrows<TimeoutException>(invokeTask);

        var cancelPacket = await transport.Session.WaitForSentPacket(PacketType.Cancel);
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "cancel should target same request");
    }

    [Test]
    public async Task InvokeCancellableNoPayloadAsyncTimeoutAndUserCancelShouldSendSingleCancel()
    {
        var transport = new FakeTransport();
        var serializer = new NoopSerializer();
        using var client = new SharpLinkClient(
            transport,
            serializer,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(80));

        Ensure(await client.ConnectAsync(), "connect");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(80));

        var invokeTask = client.InvokeCancellableNoPayloadAsync<int>(1, 2, cts.Token).AsTask();
        var callPacket = await transport.Session.WaitForSentPacket(PacketType.RpcCall);
        await EnsureThrows<Exception>(invokeTask);

        var cancelPacket = await transport.Session.WaitForSentPacket(PacketType.Cancel);
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "first cancel should target same request");
        var hasSecondCancel = await transport.Session.TryWaitForSentPacket(PacketType.Cancel, TimeSpan.FromMilliseconds(200));
        Ensure(!hasSecondCancel, "cancel packet should be sent only once");
    }

    private static async Task EnsureThrows<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeTransport : ITransport
    {
        public FakeSession Session { get; } = new();

        public async Task<IRpcSession> ConnectAsync(ISerializer serializer, CancellationToken ct = default)
        {
            Session.Serializer = serializer;
            await Session.InjectPacketAsync(PacketType.Handshake, PacketFlags.None, 0);
            return Session;
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }

    private sealed class FakeSession : IRpcSession
    {
        private readonly Pipe _pipe = new();
        private readonly Channel<PacketHeader> _sentPackets = Channel.CreateUnbounded<PacketHeader>();
        private int _disposed;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public DateTime LastActive { get; set; } = DateTime.UtcNow;
        public PipeReader Input => _pipe.Reader;
        public ISerializer Serializer { get; set; } = new NoopSerializer();
        public IStreamManager StreamManager { get; } = new StreamManager();
        public bool IsConnected => Volatile.Read(ref _disposed) == 0;

        public async ValueTask SendPacketAsync(ArrayBufferWriter<byte> packet)
        {
            var seq = new ReadOnlySequence<byte>(packet.WrittenMemory);
            var ok = PacketHelper.TryReadMessage(ref seq, out var header, out _);
            if (ok)
                await _sentPackets.Writer.WriteAsync(header);
            BufferWriterPool.Return(packet);
        }

        public async Task InjectPacketAsync(PacketType type, PacketFlags flags, long requestId)
        {
            var writer = BufferWriterPool.Get();
            try
            {
                writer.WritePacket(type, flags, requestId);
                var mem = writer.WrittenMemory;
                await _pipe.Writer.WriteAsync(mem);
                await _pipe.Writer.FlushAsync();
            }
            finally
            {
                BufferWriterPool.Return(writer);
            }
        }

        public async Task<PacketHeader> WaitForSentPacket(PacketType type)
        {
            while (true)
            {
                var header = await _sentPackets.Reader.ReadAsync();
                if (header.Type == type)
                    return header;
            }
        }

        public async Task<bool> TryWaitForSentPacket(PacketType type, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var header = await _sentPackets.Reader.ReadAsync(cts.Token);
                    if (header.Type == type)
                        return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _pipe.Writer.Complete();
            _pipe.Reader.Complete();
            _sentPackets.Writer.TryComplete();
        }
    }

    private sealed class NoopSerializer : ISerializer
    {
        public void Serialize<T>(in T value, IBufferWriter<byte> writer)
        {
        }

        public T? Deserialize<T>(ref ReadOnlySequence<byte> sequence) => default;
    }
}
